using System.Diagnostics;
using Optimizer.Core.Changes;
using Optimizer.Core.Sessions;
using Optimizer.Platform.Windows.Changes;
using Optimizer.Platform.Windows.Sessions;

namespace Optimizer.Platform.Windows.Tests;

/// <summary>
/// The real sampler, executor and monitor against two throw-away processes: copies of
/// ping.exe with unique names, one acting as "the game" and one as a background app.
/// Nothing else on the machine matches these names, so no real app is touched.
/// </summary>
public sealed class SessionEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "optimizer-tests", Guid.NewGuid().ToString("N"));
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6];
    private readonly List<Process> _started = [];

    public SessionEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try { p.Kill(); p.WaitForExit(2000); } catch (InvalidOperationException) { }
            p.Dispose();
        }

        try { Directory.Delete(_dir, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private Process StartCopy(string name)
    {
        var exe = Path.Combine(_dir, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            ArgumentList = { "-n", "60", "127.0.0.1" },
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        _started.Add(p);
        return p;
    }

    [Fact]
    public void Real_session_throttles_background_app_and_restores_it_when_game_exits()
    {
        var gameName = $"potest-game-{_tag}";
        var bgName = $"potest-bg-{_tag}";
        var background = StartCopy(bgName);
        var normalPriority = background.PriorityClass;

        var settings = new AppSettings { UserGames = [Path.Combine(_dir, gameName + ".exe")], EfficiencyModeApps = [bgName] };
        var engine = new TweakEngine(new WindowsChangeExecutor(), new Journal(Path.Combine(_dir, "journal")), "test");
        var store = new SessionStore(Path.Combine(_dir, "sessions"));
        using var monitor = new SessionMonitor(
            new WindowsSystemSampler(),
            () => new GameMatcher([], settings.UserGames, []),
            () => settings,
            engine,
            store);

        monitor.Tick();
        Assert.Null(monitor.Current); // no game yet

        var game = StartCopy(gameName);
        monitor.Tick();
        Assert.Equal(game.Id, monitor.Current?.GamePid);

        background.Refresh();
        Assert.Equal(ProcessPriorityClass.Idle, background.PriorityClass);

        for (var i = 0; i < 4; i++)
        {
            Thread.Sleep(300);
            monitor.Tick();
        }

        game.Kill();
        game.WaitForExit();
        monitor.Tick();

        Assert.Null(monitor.Current);
        background.Refresh();
        Assert.Equal(normalPriority, background.PriorityClass);

        var report = Assert.Single(store.LoadAll());
        Assert.Equal(gameName, report.GameName);
        Assert.Contains(bgName, report.EfficiencyApplied);
        Assert.NotNull(report.Cpu);
        Assert.All(engine.Journal.LoadAll(), e => Assert.Equal(JournalStatus.Reverted, e.Status));
    }
}
