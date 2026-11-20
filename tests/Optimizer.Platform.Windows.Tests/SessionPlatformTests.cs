using System.Diagnostics;
using Optimizer.Core.Changes;
using Optimizer.Platform.Windows.Changes;
using Optimizer.Platform.Windows.Sessions;

namespace Optimizer.Platform.Windows.Tests;

/// <summary>Runs against a throw-away child process that this test starts and kills.</summary>
public sealed class SessionPlatformTests : IDisposable
{
    private readonly Process _child = Process.Start(new ProcessStartInfo
    {
        FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
        ArgumentList = { "-n", "60", "127.0.0.1" },
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
    })!;

    public void Dispose()
    {
        try { _child.Kill(); } catch (InvalidOperationException) { }
        _child.Dispose();
    }

    [Fact]
    public void Sampler_sees_the_child_with_path_and_start_time()
    {
        using var sampler = new WindowsSystemSampler();
        sampler.Sample(detailed: true);
        Thread.Sleep(500);
        var sample = sampler.Sample(detailed: true);

        var child = Assert.Single(sample.Processes, p => p.Pid == _child.Id);
        Assert.Equal("PING", child.Name, ignoreCase: true);
        Assert.EndsWith(@"\ping.exe", child.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(_child.StartTime.ToUniversalTime(), child.StartTimeUtc, TimeSpan.FromSeconds(1));
        Assert.True(child.WorkingSetBytes > 0);
        Assert.NotNull(sample.Cpu);
    }

    [Fact]
    public void Efficiency_mode_applies_and_restores_priority()
    {
        using var sampler = new WindowsSystemSampler();
        var me = sampler.Sample(detailed: false).Processes.Single(p => p.Pid == _child.Id);
        var executor = new WindowsChangeExecutor();
        var original = _child.PriorityClass;

        var change = executor.Capture(new ProcessEfficiencyChange(me.Pid, me.Name, me.StartTimeUtc, null));
        Assert.Equal(ChangeState.Original, executor.Inspect(change));

        executor.Apply(change);
        _child.Refresh();
        Assert.Equal(ProcessPriorityClass.Idle, _child.PriorityClass);
        Assert.Equal(ChangeState.Applied, executor.Inspect(change));

        executor.Revert(change);
        _child.Refresh();
        Assert.Equal(original, _child.PriorityClass);
        Assert.Equal(ChangeState.Original, executor.Inspect(change));
    }

    [Fact]
    public void Exited_process_is_treated_as_nothing_to_revert()
    {
        using var sampler = new WindowsSystemSampler();
        var me = sampler.Sample(detailed: false).Processes.Single(p => p.Pid == _child.Id);
        var executor = new WindowsChangeExecutor();
        var change = executor.Capture(new ProcessEfficiencyChange(me.Pid, me.Name, me.StartTimeUtc, null));
        executor.Apply(change);

        _child.Kill();
        _child.WaitForExit();

        Assert.Equal(ChangeState.Original, executor.Inspect(change));
        executor.Revert(change); // must not throw
    }

    [Fact]
    public void Wrong_start_time_is_never_touched()
    {
        var executor = new WindowsChangeExecutor();
        var impostor = new ProcessEfficiencyChange(_child.Id, "PING", DateTime.UtcNow.AddDays(-1), null);

        Assert.Throws<InvalidOperationException>(() => executor.Capture(impostor));
    }
}
