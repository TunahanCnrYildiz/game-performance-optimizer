namespace Optimizer.Core.Sessions;

/// <summary>Accumulates samples into a <see cref="SessionReport"/>. Pure; no system access.</summary>
public sealed class SessionAggregator(string gameName, string gamePath, string gameProcessName, DateTimeOffset startedAt)
{
    /// <summary>Processes that are part of Windows' idle accounting, not real consumers.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "Idle", "System Idle Process", "_Total" };

    private const int MaxBackgroundEntries = 25;

    private readonly Dictionary<string, Accumulator> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CpuSample> _cpu = [];
    private int _samples;

    public int SampleCount => _samples;

    public void Add(SystemSample sample)
    {
        _samples++;
        if (sample.Cpu is { } cpu)
            _cpu.Add(cpu);

        foreach (var group in sample.Processes.Where(p => !Ignored.Contains(p.Name)).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!_byName.TryGetValue(group.Key, out var acc))
                _byName[group.Key] = acc = new Accumulator(group.Key);

            var cpuSum = group.Sum(p => p.CpuPercent);
            var gpuSum = Math.Min(100, group.Sum(p => p.GpuPercent));
            acc.CpuTotal += cpuSum;
            acc.CpuMax = Math.Max(acc.CpuMax, cpuSum);
            acc.GpuTotal += gpuSum;
            acc.GpuMax = Math.Max(acc.GpuMax, gpuSum);
            acc.IoTotal += group.Sum(p => p.IoBytesPerSec);
            acc.WorkingSetPeak = Math.Max(acc.WorkingSetPeak, group.Sum(p => p.WorkingSetBytes));
            acc.MaxInstances = Math.Max(acc.MaxInstances, group.Count());
        }
    }

    public SessionReport Build(DateTimeOffset endedAt, IReadOnlyList<string>? efficiencyApplied = null)
    {
        // Average over all samples, so a process that ran only briefly is not overstated.
        var n = Math.Max(_samples, 1);
        var usages = _byName.Values
            .Select(a => new ProcessUsage(
                a.Name,
                string.Equals(a.Name, gameProcessName, StringComparison.OrdinalIgnoreCase),
                a.CpuTotal / n,
                a.CpuMax,
                a.GpuTotal / n,
                a.GpuMax,
                a.IoTotal / n,
                a.WorkingSetPeak,
                a.MaxInstances))
            .ToList();

        return new SessionReport
        {
            Id = Guid.NewGuid(),
            GameName = gameName,
            GamePath = gamePath,
            StartedAt = startedAt,
            EndedAt = endedAt,
            SampleCount = _samples,
            Cpu = BuildCpu(),
            Game = usages.FirstOrDefault(u => u.IsGame),
            Background = usages
                .Where(u => !u.IsGame)
                .OrderByDescending(Score)
                .Where(u => Score(u) > 0.05)
                .Take(MaxBackgroundEntries)
                .ToList(),
            EfficiencyApplied = efficiencyApplied ?? [],
        };
    }

    /// <summary>Rough "how much does this compete with the game": CPU% + GPU% + 1 point per 1 MB/s of I/O.</summary>
    public static double Score(ProcessUsage u) => u.AvgCpuPercent + u.AvgGpuPercent + u.AvgIoBytesPerSec / (1024 * 1024);

    private CpuSummary? BuildCpu()
    {
        if (_cpu.Count == 0)
            return null;

        var withClock = _cpu.Where(c => c.EffectiveMhz > 0).ToList();
        return new CpuSummary(
            _cpu.Average(c => c.UtilizationPercent),
            withClock.Count > 0 ? withClock.Average(c => c.EffectiveMhz) : 0,
            withClock.Count > 0 ? withClock.Min(c => c.EffectiveMhz) : 0,
            _cpu.Average(c => c.PerformancePercent),
            100.0 * _cpu.Count(c => c.IsLimited) / _cpu.Count,
            _cpu.Min(c => c.AllowedPerformancePercent));
    }

    private sealed class Accumulator(string name)
    {
        public string Name { get; } = name;
        public double CpuTotal;
        public double CpuMax;
        public double GpuTotal;
        public double GpuMax;
        public double IoTotal;
        public long WorkingSetPeak;
        public int MaxInstances;
    }
}
