namespace Optimizer.Core.Benchmarks;

/// <summary>One presented frame, as reported by PresentMon. Times are milliseconds.</summary>
public sealed record FrameRecord(
    string Application,
    int ProcessId,
    string SwapChain,
    double MsBetweenPresents,
    double? MsCpuBusy,
    double? MsGpuBusy,
    bool Displayed);

public enum Bottleneck { Unknown, Gpu, Cpu, FrameCap }

public sealed record FrameSummary
{
    public required int Frames { get; init; }
    public required double DurationSeconds { get; init; }
    public required double AvgFps { get; init; }

    /// <summary>FPS at the 99th-percentile frame time (only 1% of frames were slower).</summary>
    public required double Low1PercentFps { get; init; }

    /// <summary>FPS at the 99.9th-percentile frame time.</summary>
    public required double Low01PercentFps { get; init; }

    public required double MedianFrameMs { get; init; }
    public required double P99FrameMs { get; init; }
    public required double MaxFrameMs { get; init; }

    /// <summary>Frames taking more than <see cref="FrameStatistics.StutterFactor"/> × the median.</summary>
    public required int Stutters { get; init; }

    public required double DroppedPercent { get; init; }
    public double? AvgCpuBusyMs { get; init; }
    public double? AvgGpuBusyMs { get; init; }
    public Bottleneck Bottleneck { get; init; }
}

public static class FrameStatistics
{
    public const double StutterFactor = 2.5;

    /// <summary>
    /// Frames of the busiest swap chain only: games sometimes present a launcher overlay or a
    /// second window, which would otherwise mix two frame rates.
    /// </summary>
    public static IReadOnlyList<FrameRecord> MainSwapChain(IEnumerable<FrameRecord> frames) =>
        frames.GroupBy(f => (f.ProcessId, f.SwapChain))
            .MaxBy(g => g.Count())
            ?.ToList() ?? [];

    public static FrameSummary? Compute(IReadOnlyList<FrameRecord> frames)
    {
        // The first frame's interval measures time since capture start, not a real frame.
        var times = frames.Skip(1).Select(f => f.MsBetweenPresents).Where(t => t > 0 && t < 5000).ToList();
        if (times.Count < 10)
            return null;

        var sorted = times.Order().ToList();
        var total = times.Sum();
        var median = Percentile(sorted, 50);
        var p99 = Percentile(sorted, 99);
        var p999 = Percentile(sorted, 99.9);

        var cpu = Average(frames.Select(f => f.MsCpuBusy));
        var gpu = Average(frames.Select(f => f.MsGpuBusy));
        var avgFrame = total / times.Count;

        return new FrameSummary
        {
            Frames = times.Count,
            DurationSeconds = total / 1000,
            AvgFps = 1000 * times.Count / total,
            Low1PercentFps = 1000 / p99,
            Low01PercentFps = 1000 / p999,
            MedianFrameMs = median,
            P99FrameMs = p99,
            MaxFrameMs = sorted[^1],
            Stutters = times.Count(t => t > median * StutterFactor),
            DroppedPercent = 100.0 * frames.Count(f => !f.Displayed) / frames.Count,
            AvgCpuBusyMs = cpu,
            AvgGpuBusyMs = gpu,
            Bottleneck = IsFrameCapped(median, p99) ? Bottleneck.FrameCap : Classify(avgFrame, cpu, gpu),
        };
    }

    /// <summary>
    /// GPU busy close to the frame time means the GPU is the limit; CPU busy close to it with an
    /// idle GPU means the CPU is; neither means something else (a frame cap or V-Sync) sets the pace.
    /// </summary>
    internal static Bottleneck Classify(double avgFrameMs, double? cpuBusy, double? gpuBusy)
    {
        if (cpuBusy is null || gpuBusy is null || avgFrameMs <= 0)
            return Bottleneck.Unknown;

        var gpuShare = gpuBusy.Value / avgFrameMs;
        var cpuShare = cpuBusy.Value / avgFrameMs;
        if (gpuShare >= 0.9)
            return Bottleneck.Gpu;
        if (cpuShare >= 0.9 && gpuShare < 0.8)
            return Bottleneck.Cpu;
        return gpuShare < 0.8 && cpuShare < 0.8 ? Bottleneck.FrameCap : Bottleneck.Unknown;
    }

    /// <summary>Common frame-rate caps: V-Sync at usual refresh rates and typical in-game limits.</summary>
    private static readonly int[] CommonCaps = [30, 60, 72, 75, 90, 100, 120, 144, 165, 170, 180, 200, 240, 280, 360];

    /// <summary>
    /// Frames locked tightly to a common rate. In that case PresentMon's "CPU busy" also
    /// counts the limiter's waiting, so it must not be read as a CPU bottleneck.
    /// </summary>
    internal static bool IsFrameCapped(double medianMs, double p99Ms)
    {
        if (medianMs <= 0 || p99Ms / medianMs > 1.15)
            return false;

        var fps = 1000 / medianMs;
        return CommonCaps.Any(cap => Math.Abs(fps - cap) / cap < 0.02);
    }

    /// <summary>Linear interpolation between closest ranks.</summary>
    internal static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 1)
            return sorted[0];

        var rank = percentile / 100 * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (rank - lower);
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var list = values.OfType<double>().Where(v => v >= 0).ToList();
        return list.Count == 0 ? null : list.Average();
    }
}
