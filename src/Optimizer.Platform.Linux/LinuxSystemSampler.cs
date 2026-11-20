using System.Diagnostics;
using System.Globalization;
using Optimizer.Core.Sessions;

namespace Optimizer.Platform.Linux;

/// <summary>
/// Per-process CPU, memory, I/O and GPU use from /proc, like `top` and `nvtop` do. GPU time
/// comes from the DRM fdinfo counters (amdgpu, i915, xe, recent nvidia). Other users'
/// processes are listed without I/O and GPU, which the kernel only shows to their owner.
/// </summary>
public sealed class LinuxSystemSampler(LinuxFs? fs = null) : ISystemSampler
{
    private readonly LinuxFs _fs = fs ?? LinuxFs.System;
    private readonly Dictionary<int, Tracked> _tracked = [];
    private readonly int _cpus = Environment.ProcessorCount;
    private readonly long _ticksPerSecond = 100; // USER_HZ is 100 on every mainstream architecture
    private long _lastTimestamp;
    private (long Busy, long Total)? _lastCpu;
    private long? _lastThrottle;

    public SystemSample Sample(bool detailed)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = _lastTimestamp == 0 ? 0 : (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;

        var bootTime = BootTime();
        var seen = new HashSet<int>();
        var samples = new List<ProcessSample>();

        foreach (var dir in _fs.Directories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
                continue;

            var stat = _fs.ReadLine($"{dir}/stat");
            if (stat is null || ParseStat(stat) is not { } parsed)
                continue;

            seen.Add(pid);
            if (!_tracked.TryGetValue(pid, out var t) || t.StartTicks != parsed.StartTicks)
                _tracked[pid] = t = new Tracked(parsed.StartTicks, ResolveIdentity(dir, parsed.Comm));

            var cpuTicks = parsed.UTime + parsed.STime;
            double cpu = 0, io = 0, gpu = 0;
            if (t.LastCpuTicks >= 0 && elapsedSeconds > 0)
                cpu = Math.Clamp((cpuTicks - t.LastCpuTicks) / (double)_ticksPerSecond / (elapsedSeconds * _cpus) * 100, 0, 100);
            t.LastCpuTicks = cpuTicks;

            if (ReadIoBytes(dir) is { } bytes)
            {
                if (t.LastIo >= 0 && elapsedSeconds > 0)
                    io = Math.Max(0, (bytes - t.LastIo) / elapsedSeconds);
                t.LastIo = bytes;
            }

            if (detailed && ReadGpuNs(dir) is { } gpuNs)
            {
                if (t.LastGpuNs >= 0 && elapsedSeconds > 0)
                    gpu = Math.Clamp((gpuNs - t.LastGpuNs) / (elapsedSeconds * 1e9) * 100, 0, 100);
                t.LastGpuNs = gpuNs;
            }

            samples.Add(new ProcessSample(
                pid,
                t.Name,
                t.Path,
                bootTime.AddSeconds(parsed.StartTicks / (double)_ticksPerSecond),
                cpu,
                gpu,
                io,
                parsed.RssPages * Environment.SystemPageSize));
        }

        foreach (var gone in _tracked.Keys.Where(k => !seen.Contains(k)).ToList())
            _tracked.Remove(gone);

        return new SystemSample(DateTimeOffset.Now, detailed ? ReadCpu() : null, samples);
    }

    /// <summary>
    /// For Wine/Proton the executable is wine64-preloader; the Windows .exe is the first
    /// argument (often "Z:\home\...\Game.exe"), which is what the user knows as the game.
    /// </summary>
    private (string Name, string? Path) ResolveIdentity(string dir, string comm)
    {
        var exe = _fs.LinkTarget($"{dir}/exe");
        var cmdline = _fs.Read($"{dir}/cmdline")?.Split('\0', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var winExe = cmdline.FirstOrDefault(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (winExe is not null && (exe is null || exe.Contains("wine", StringComparison.OrdinalIgnoreCase) || exe.Contains("proton", StringComparison.OrdinalIgnoreCase)))
        {
            var unix = winExe.Length > 2 && winExe[1] == ':' ? winExe[2..].Replace('\\', '/') : winExe.Replace('\\', '/');
            return (Path.GetFileName(unix), unix);
        }

        return (comm, exe);
    }

    internal static (string Comm, long UTime, long STime, long StartTicks, long RssPages)? ParseStat(string stat)
    {
        // "123 (some name) S 1 ..." — the name may contain spaces and parentheses; fields resume after the last ')'.
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 0 || close < open)
            return null;

        var rest = stat[(close + 2)..].Split(' ');
        // rest[0] is field 3 (state); utime=14, stime=15, starttime=22, rss=24 → indices 11, 12, 19, 21.
        if (rest.Length < 22)
            return null;
        return (stat[(open + 1)..close], long.Parse(rest[11], CultureInfo.InvariantCulture), long.Parse(rest[12], CultureInfo.InvariantCulture),
            long.Parse(rest[19], CultureInfo.InvariantCulture), long.Parse(rest[21], CultureInfo.InvariantCulture));
    }

    private long? ReadIoBytes(string dir)
    {
        var text = _fs.Read($"{dir}/io");
        if (text is null)
            return null;
        long total = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("read_bytes:", StringComparison.Ordinal) || line.StartsWith("write_bytes:", StringComparison.Ordinal))
                total += long.Parse(line[(line.IndexOf(':') + 1)..].Trim(), CultureInfo.InvariantCulture);
        }

        return total;
    }

    /// <summary>Sum of drm-engine-* nanoseconds over this process's DRM clients (each client counted once).</summary>
    private long? ReadGpuNs(string dir)
    {
        string[] files;
        try
        {
            var path = _fs.Path($"{dir}/fdinfo");
            if (!Directory.Exists(path))
                return null;
            files = Directory.GetFiles(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var clients = new Dictionary<string, long>();
        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!text.Contains("drm-client-id", StringComparison.Ordinal))
                continue;

            string? client = null;
            long ns = 0;
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("drm-client-id:", StringComparison.Ordinal))
                    client = line[14..].Trim();
                else if (line.StartsWith("drm-engine-", StringComparison.Ordinal) && !line.StartsWith("drm-engine-capacity", StringComparison.Ordinal))
                {
                    var value = line[(line.IndexOf(':') + 1)..].Trim().Split(' ')[0];
                    if (long.TryParse(value, out var v))
                        ns += v;
                }
            }

            if (client is not null)
                clients[client] = ns;
        }

        return clients.Count == 0 ? null : clients.Values.Sum();
    }

    private CpuSample? ReadCpu()
    {
        var first = _fs.ReadLine("/proc/stat");
        if (first is null || !first.StartsWith("cpu ", StringComparison.Ordinal))
            return null;

        var values = first.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(v => long.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        var total = values.Sum();
        var busy = total - idle;
        double util = 0;
        if (_lastCpu is { } last && total > last.Total)
            util = (busy - last.Busy) * 100.0 / (total - last.Total);
        _lastCpu = (busy, total);

        var freqs = Enumerable.Range(0, _cpus)
            .Select(i => _fs.ReadLong($"/sys/devices/system/cpu/cpu{i}/cpufreq/scaling_cur_freq"))
            .OfType<long>()
            .ToList();
        var mhz = freqs.Count == 0 ? 0 : freqs.Average() / 1000;
        var baseKhz = _fs.ReadLong("/sys/devices/system/cpu/cpu0/cpufreq/base_frequency")
                      ?? _fs.ReadLong("/sys/devices/system/cpu/cpu0/acpi_cppc/nominal_freq") * 1000;
        var perf = baseKhz is > 0 && mhz > 0 ? mhz * 1000 / baseKhz.Value * 100 : 100;

        // Intel reports thermal throttling events; a rising count during the sample means the CPU was held back.
        var throttle = _fs.ReadLong("/sys/devices/system/cpu/cpu0/thermal_throttle/package_throttle_count");
        uint flags = throttle is { } now && _lastThrottle is { } before && now > before ? 1u : 0u;
        _lastThrottle = throttle;

        return new CpuSample(util, perf, 100, mhz, flags);
    }

    private DateTime BootTime()
    {
        var line = (_fs.Read("/proc/stat") ?? "").Split('\n').FirstOrDefault(l => l.StartsWith("btime ", StringComparison.Ordinal));
        return line is not null && long.TryParse(line[6..].Trim(), out var seconds)
            ? DateTime.UnixEpoch.AddSeconds(seconds)
            : DateTime.UnixEpoch;
    }

    public void Dispose()
    {
    }

    private sealed class Tracked(long startTicks, (string Name, string? Path) identity)
    {
        public long StartTicks { get; } = startTicks;
        public string Name { get; } = identity.Name;
        public string? Path { get; } = identity.Path;
        public long LastCpuTicks { get; set; } = -1;
        public long LastIo { get; set; } = -1;
        public long LastGpuNs { get; set; } = -1;
    }
}
