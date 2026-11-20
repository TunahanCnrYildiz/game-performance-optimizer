namespace Optimizer.Core.Sessions;

/// <summary>One process at one moment. Percentages are of the whole machine, like Task Manager.</summary>
public sealed record ProcessSample(
    int Pid,
    string Name,
    string? Path,
    DateTime StartTimeUtc,
    double CpuPercent,
    double GpuPercent,
    double IoBytesPerSec,
    long WorkingSetBytes);

/// <param name="PerformancePercent">Current clock as a percentage of base clock (boost shows above 100).</param>
/// <param name="AllowedPerformancePercent">
/// Windows' "% Performance Limit": share of maximum performance the CPU is currently allowed.
/// 100 means unrestricted; lower means a power, thermal or other limit is active.
/// </param>
/// <param name="LimitFlags">Windows' "Performance Limit Flags"; non-zero when a limit is active.</param>
public sealed record CpuSample(
    double UtilizationPercent,
    double PerformancePercent,
    double AllowedPerformancePercent,
    double EffectiveMhz,
    uint LimitFlags = 0)
{
    /// <summary>True when the CPU reports being held below its maximum.</summary>
    public bool IsLimited => LimitFlags != 0 || AllowedPerformancePercent < 95;
}

public sealed record SystemSample(DateTimeOffset At, CpuSample? Cpu, IReadOnlyList<ProcessSample> Processes);

public interface ISystemSampler : IDisposable
{
    /// <param name="detailed">
    /// False: cheap scan (names, paths, CPU, memory) used to detect a game.
    /// True: also GPU usage and CPU clock/limits, used during a session.
    /// </param>
    SystemSample Sample(bool detailed);
}
