using Optimizer.Core.Benchmarks;

namespace Optimizer.Core.Tests;

public class BenchmarkTests
{
    private static FrameRecord F(double ms, double? cpu = null, double? gpu = null, bool displayed = true, string chain = "0x1") =>
        new("game.exe", 10, chain, ms, cpu, gpu, displayed);

    private static List<FrameRecord> Steady(int count, double ms, double? cpu = null, double? gpu = null) =>
        Enumerable.Range(0, count).Select(_ => F(ms, cpu, gpu)).ToList();

    [Fact]
    public void Steady_10ms_frames_are_100_fps_with_no_stutter()
    {
        var s = FrameStatistics.Compute(Steady(1001, 10))!;

        Assert.Equal(1000, s.Frames); // first frame skipped
        Assert.Equal(100, s.AvgFps, 6);
        Assert.Equal(100, s.Low1PercentFps, 6);
        Assert.Equal(0, s.Stutters);
    }

    [Fact]
    public void Slow_frames_drag_down_1_percent_low_but_barely_the_average()
    {
        var frames = Steady(1001, 10);
        for (var i = 1; i <= 20; i++)
            frames[i * 40] = F(50); // 2% of frames take 50 ms

        var s = FrameStatistics.Compute(frames)!;

        Assert.InRange(s.AvgFps, 90, 95);
        Assert.Equal(20, s.Low1PercentFps, 6); // P99 lands on a 50 ms frame
        Assert.Equal(20, s.Stutters);
    }

    [Fact]
    public void Too_few_frames_yield_no_summary() =>
        Assert.Null(FrameStatistics.Compute(Steady(5, 10)));

    [Fact]
    public void Dropped_frames_are_counted()
    {
        var frames = Steady(101, 10);
        frames[50] = F(10, displayed: false);

        Assert.Equal(100.0 / 101, FrameStatistics.Compute(frames)!.DroppedPercent, 6);
    }

    [Fact]
    public void Main_swap_chain_ignores_a_small_overlay()
    {
        var frames = Steady(200, 10).Concat(Enumerable.Range(0, 20).Select(_ => F(33, chain: "0x2"))).ToList();

        Assert.All(FrameStatistics.MainSwapChain(frames), f => Assert.Equal("0x1", f.SwapChain));
    }

    [Theory]
    [InlineData(10, 4, 9.5, Bottleneck.Gpu)]
    [InlineData(10, 9.5, 5, Bottleneck.Cpu)]
    [InlineData(16.7, 4, 5, Bottleneck.FrameCap)]
    public void Bottleneck_classification(double frame, double cpu, double gpu, Bottleneck expected) =>
        Assert.Equal(expected, FrameStatistics.Classify(frame, cpu, gpu));

    [Fact]
    public void Steady_60_fps_with_high_cpu_busy_is_a_frame_cap_not_cpu_bound()
    {
        // Seen in a real capture: a 60 FPS-locked app reports CPU busy ≈ frame time.
        var s = FrameStatistics.Compute(Steady(500, 16.68, cpu: 16.4, gpu: 1.9))!;

        Assert.Equal(Bottleneck.FrameCap, s.Bottleneck);
    }

    [Theory]
    [InlineData(16.67, 17.5, true)]   // 60 FPS, tight
    [InlineData(6.94, 7.3, true)]     // 144 FPS
    [InlineData(16.67, 25, false)]    // 60 FPS median but uneven
    [InlineData(9.0, 9.4, false)]     // 111 FPS, not a common cap
    public void Frame_cap_detection(double median, double p99, bool expected) =>
        Assert.Equal(expected, FrameStatistics.IsFrameCapped(median, p99));

    [Fact]
    public void Percentile_interpolates()
    {
        Assert.Equal(5.5, FrameStatistics.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 50), 6);
        Assert.Equal(10, FrameStatistics.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 100), 6);
    }

    // ---- Comparison ----

    private static BenchmarkRun Run(string label, double avg, double low) => new()
    {
        Id = Guid.NewGuid(),
        GameName = "Game",
        Label = label,
        CapturedAt = DateTimeOffset.Now,
        Summary = new FrameSummary
        {
            Frames = 1000, DurationSeconds = 10, AvgFps = avg, Low1PercentFps = low, Low01PercentFps = low,
            MedianFrameMs = 1000 / avg, P99FrameMs = 1000 / low, MaxFrameMs = 50, Stutters = 0, DroppedPercent = 0,
        },
    };

    [Fact]
    public void Difference_within_run_to_run_spread_is_no_difference()
    {
        // A runs vary by ~6%; B is 3% higher on average → not a real difference.
        var a = new[] { Run("A", 100, 70), Run("A", 106, 72) };
        var b = new[] { Run("B", 106, 71), Run("B", 106, 73) };

        var r = BenchmarkComparison.Compare("A", a, "B", b);

        Assert.Equal(Verdict.NoDifference, r.AvgFps.Verdict);
        Assert.False(r.NoiseAssumed);
    }

    [Fact]
    public void Consistent_large_gain_is_reported()
    {
        var a = new[] { Run("A", 100, 70), Run("A", 101, 71) };
        var b = new[] { Run("B", 115, 85), Run("B", 116, 86) };

        var r = BenchmarkComparison.Compare("A", a, "B", b);

        Assert.Equal(Verdict.BBetter, r.AvgFps.Verdict);
        Assert.Equal(Verdict.BBetter, r.Low1PercentFps.Verdict);
    }

    [Fact]
    public void Single_runs_assume_5_percent_noise()
    {
        var r = BenchmarkComparison.Compare("A", [Run("A", 100, 70)], "B", [Run("B", 104, 72)]);

        Assert.True(r.NoiseAssumed);
        Assert.Equal(Verdict.NoDifference, r.AvgFps.Verdict);
        Assert.Equal(BenchmarkComparison.DefaultNoisePercent, r.AvgFps.NoisePercent);
    }

    [Fact]
    public void Identical_repeats_still_need_at_least_2_percent()
    {
        var r = BenchmarkComparison.Compare("A", [Run("A", 100, 70), Run("A", 100, 70)], "B", [Run("B", 98.5, 69), Run("B", 98.5, 69)]);

        Assert.Equal(Verdict.NoDifference, r.AvgFps.Verdict);
    }
}
