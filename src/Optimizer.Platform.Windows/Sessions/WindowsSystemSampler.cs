using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Optimizer.Core.Sessions;
using Optimizer.Platform.Windows.Interop;

namespace Optimizer.Platform.Windows.Sessions;

/// <summary>
/// Samples per-process CPU, memory, I/O and GPU use plus CPU clock/limits, without
/// administrator rights. Process handles are cached per PID and start time; GPU usage
/// comes from the "GPU Engine" performance counters, the same source Task Manager uses.
/// Not thread-safe; one caller at a time (the session monitor's loop).
/// </summary>
public sealed partial class WindowsSystemSampler : ISystemSampler
{
    private readonly Dictionary<int, Tracked> _tracked = [];
    private readonly int _logicalProcessors = Environment.ProcessorCount;
    private readonly PdhQuery? _pdh;
    private readonly IntPtr _cpuTime, _cpuPerf, _cpuLimit, _cpuFlags, _cpuFreq, _gpu;
    private long _lastTimestamp;

    public WindowsSystemSampler()
    {
        try
        {
            _pdh = new PdhQuery();
            _pdh.TryAdd(@"\Processor Information(_Total)\% Processor Time", out _cpuTime);
            _pdh.TryAdd(@"\Processor Information(_Total)\% Processor Performance", out _cpuPerf);
            _pdh.TryAdd(@"\Processor Information(_Total)\% Performance Limit", out _cpuLimit);
            _pdh.TryAdd(@"\Processor Information(_Total)\Performance Limit Flags", out _cpuFlags);
            _pdh.TryAdd(@"\Processor Information(_Total)\Processor Frequency", out _cpuFreq);
            _pdh.TryAdd(@"\GPU Engine(*)\Utilization Percentage", out _gpu);
        }
        catch (InvalidOperationException)
        {
            // Counters unavailable (rare, e.g. corrupted perf registry): sample processes only.
            _pdh?.Dispose();
            _pdh = null;
        }
    }

    public SystemSample Sample(bool detailed)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed100ns = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) * 10_000_000.0 / Stopwatch.Frequency;
        _lastTimestamp = now;

        CpuSample? cpu = null;
        Dictionary<int, double>? gpuByPid = null;
        if (detailed && _pdh is not null)
        {
            _pdh.Collect();
            cpu = ReadCpu();
            gpuByPid = ReadGpu();
        }

        var seen = new HashSet<int>();
        var samples = new List<ProcessSample>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var pid = process.Id;
                if (pid == 0)
                    continue;
                seen.Add(pid);

                long workingSet;
                string name;
                try
                {
                    workingSet = process.WorkingSet64;
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue; // exited between enumeration and read
                }

                var t = Track(pid, name);
                double cpuPercent = 0, ioRate = 0;

                if (t.Handle is { IsInvalid: false } handle)
                {
                    if (ProcessNative.GetProcessTimes(handle, out _, out _, out var kernel, out var user))
                    {
                        var total = kernel + user;
                        if (t.LastCpu >= 0 && elapsed100ns > 0)
                            cpuPercent = Math.Clamp((total - t.LastCpu) / (elapsed100ns * _logicalProcessors) * 100, 0, 100);
                        t.LastCpu = total;
                    }

                    if (ProcessNative.GetProcessIoCounters(handle, out var io))
                    {
                        var bytes = (long)(io.ReadTransferCount + io.WriteTransferCount);
                        if (t.LastIo >= 0 && elapsed100ns > 0)
                            ioRate = Math.Max(0, (bytes - t.LastIo) / (elapsed100ns / 10_000_000.0));
                        t.LastIo = bytes;
                    }
                }

                samples.Add(new ProcessSample(
                    pid,
                    name,
                    t.Path,
                    t.StartTimeUtc,
                    cpuPercent,
                    gpuByPid is not null && gpuByPid.TryGetValue(pid, out var g) ? g : 0,
                    ioRate,
                    workingSet));
            }
        }

        foreach (var gone in _tracked.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _tracked[gone].Handle?.Dispose();
            _tracked.Remove(gone);
        }

        return new SystemSample(DateTimeOffset.Now, cpu, samples);
    }

    private Tracked Track(int pid, string name)
    {
        if (_tracked.TryGetValue(pid, out var existing) && existing.Name == name)
            return existing;

        existing?.Handle?.Dispose();

        // Protected/system processes refuse even limited access; they are still listed, just without path/CPU/I/O.
        var handle = ProcessNative.OpenProcess(ProcessNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        Tracked t;
        if (handle.IsInvalid)
        {
            handle.Dispose();
            t = new Tracked(name, null, null, DateTime.MinValue);
        }
        else
        {
            t = new Tracked(name, handle, ProcessNative.ImagePath(handle), ProcessNative.StartTimeUtc(handle) ?? DateTime.MinValue);
        }

        _tracked[pid] = t;
        return t;
    }

    private CpuSample? ReadCpu()
    {
        var util = _pdh!.Value(_cpuTime);
        if (util is null)
            return null;

        var perf = _pdh.Value(_cpuPerf) ?? 0;
        // Missing counter = assume unrestricted rather than inventing a limit.
        var allowed = _cpuLimit == IntPtr.Zero ? 100 : _pdh.Value(_cpuLimit) ?? 100;
        var flags = _cpuFlags == IntPtr.Zero ? 0 : (uint)(_pdh.Value(_cpuFlags) ?? 0);
        var baseMhz = _pdh.Value(_cpuFreq) ?? 0;
        return new CpuSample(util.Value, perf, allowed, baseMhz * perf / 100, flags);
    }

    /// <summary>Per PID: sum over instances of each engine type, then the busiest type (Task Manager's method).</summary>
    private Dictionary<int, double>? ReadGpu()
    {
        if (_gpu == IntPtr.Zero)
            return null;

        var result = new Dictionary<int, double>();
        foreach (var byPid in _pdh!.Values(_gpu)
                     .Select(v => (Match: GpuInstanceRegex().Match(v.Instance), v.Value))
                     .Where(x => x.Match.Success)
                     .GroupBy(x => int.Parse(x.Match.Groups[1].Value)))
        {
            result[byPid.Key] = byPid
                .GroupBy(x => x.Match.Groups[2].Value)
                .Max(type => type.Sum(x => x.Value));
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var t in _tracked.Values)
            t.Handle?.Dispose();
        _tracked.Clear();
        _pdh?.Dispose();
    }

    [GeneratedRegex(@"^pid_(\d+)_.*_engtype_(.+)$")]
    private static partial Regex GpuInstanceRegex();

    private sealed class Tracked(string name, SafeProcessHandle? handle, string? path, DateTime startTimeUtc)
    {
        public string Name { get; } = name;
        public SafeProcessHandle? Handle { get; } = handle;
        public string? Path { get; } = path;
        public DateTime StartTimeUtc { get; } = startTimeUtc;
        public long LastCpu { get; set; } = -1;
        public long LastIo { get; set; } = -1;
    }
}
