namespace Optimizer.Core.Sessions;

/// <summary>Usage of every process sharing a name (e.g. all chrome.exe), averaged over the whole session.</summary>
public sealed record ProcessUsage(
    string Name,
    bool IsGame,
    double AvgCpuPercent,
    double MaxCpuPercent,
    double AvgGpuPercent,
    double MaxGpuPercent,
    double AvgIoBytesPerSec,
    long PeakWorkingSetBytes,
    int MaxInstances);

public sealed record CpuSummary(
    double AvgUtilizationPercent,
    double AvgEffectiveMhz,
    double MinEffectiveMhz,
    double AvgPerformancePercent,
    // Share of samples where the CPU reported being held back by a limit (power, thermal, ...).
    double LimitedSamplePercent,
    // Lowest "allowed performance" seen; 100 = never restricted.
    double MinAllowedPerformancePercent);

public sealed record SessionReport
{
    public required Guid Id { get; init; }
    public required string GameName { get; init; }
    public required string GamePath { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required int SampleCount { get; init; }
    public CpuSummary? Cpu { get; init; }
    public ProcessUsage? Game { get; init; }

    /// <summary>Everything except the game, highest combined usage first.</summary>
    public IReadOnlyList<ProcessUsage> Background { get; init; } = [];

    /// <summary>Apps put into efficiency mode during the session (process names).</summary>
    public IReadOnlyList<string> EfficiencyApplied { get; init; } = [];

    public TimeSpan Duration => EndedAt - StartedAt;
}
