namespace Optimizer.Core.Benchmarks;

public sealed record BenchmarkRun
{
    public required Guid Id { get; init; }
    public required string GameName { get; init; }

    /// <summary>What the user tested, e.g. "Oyun Modu açık". Runs with the same label are repeats.</summary>
    public required string Label { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
    public required FrameSummary Summary { get; init; }
}

public enum Verdict
{
    /// <summary>The difference is within run-to-run variation; no conclusion.</summary>
    NoDifference,
    BBetter,
    BWorse,
}

public sealed record MetricDelta(double A, double B, double PercentChange, double NoisePercent, Verdict Verdict);

public sealed record ComparisonResult(
    string LabelA,
    string LabelB,
    int RunsA,
    int RunsB,
    MetricDelta AvgFps,
    MetricDelta Low1PercentFps,
    // True when either side has a single run, so noise could not be measured and a default was assumed.
    bool NoiseAssumed);

/// <summary>
/// Compares two groups of runs. A change only counts when it is larger than the spread
/// between repeated runs of the same setting — otherwise it is reported as "no difference",
/// which is the honest answer for most tweaks.
/// </summary>
public static class BenchmarkComparison
{
    /// <summary>Assumed run-to-run variation when there are no repeats to measure it from.</summary>
    public const double DefaultNoisePercent = 5;

    /// <summary>Never claim a difference smaller than this, however consistent the runs look.</summary>
    public const double MinimumNoisePercent = 2;

    public static ComparisonResult Compare(string labelA, IReadOnlyList<BenchmarkRun> a, string labelB, IReadOnlyList<BenchmarkRun> b)
    {
        if (a.Count == 0 || b.Count == 0)
            throw new ArgumentException("Her iki grupta da en az bir ölçüm olmalı.");

        var assumed = a.Count < 2 || b.Count < 2;
        return new ComparisonResult(
            labelA,
            labelB,
            a.Count,
            b.Count,
            Delta(a.Select(r => r.Summary.AvgFps).ToList(), b.Select(r => r.Summary.AvgFps).ToList(), assumed),
            Delta(a.Select(r => r.Summary.Low1PercentFps).ToList(), b.Select(r => r.Summary.Low1PercentFps).ToList(), assumed),
            assumed);
    }

    private static MetricDelta Delta(IReadOnlyList<double> a, IReadOnlyList<double> b, bool assumed)
    {
        var meanA = a.Average();
        var meanB = b.Average();
        var change = meanA == 0 ? 0 : (meanB - meanA) / meanA * 100;

        var noise = assumed
            ? DefaultNoisePercent
            : Math.Max(MinimumNoisePercent, Math.Max(SpreadPercent(a), SpreadPercent(b)));

        var verdict = Math.Abs(change) <= noise ? Verdict.NoDifference : change > 0 ? Verdict.BBetter : Verdict.BWorse;
        return new MetricDelta(meanA, meanB, change, noise, verdict);
    }

    /// <summary>(max − min) / mean of repeated runs, in percent.</summary>
    internal static double SpreadPercent(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return values.Count < 2 || mean == 0 ? 0 : (values.Max() - values.Min()) / mean * 100;
    }
}
