using Optimizer.Core.Changes;
using Optimizer.Core.Sessions;

namespace Optimizer.Core.Tests;

public sealed class SessionTests : IDisposable
{
    private const string Lib = @"D:\SteamLibrary\steamapps\common";
    private static readonly DateTime T0 = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "optimizer-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ProcessSample P(int pid, string name, string? path = null, double cpu = 0, double gpu = 0, double io = 0, long ws = 100) =>
        new(pid, name, path, T0, cpu, gpu, io, ws);

    private static SystemSample S(params ProcessSample[] processes) =>
        new(DateTimeOffset.Now, new CpuSample(50, 110, 100, 4800), processes);

    private static readonly ProcessSample Game = P(100, "Game", $@"{Lib}\Game\Game.exe", cpu: 30, gpu: 90, ws: 4_000_000_000);

    // ---- GameMatcher ----

    [Fact]
    public void Matcher_finds_game_in_library_and_ignores_helpers()
    {
        var matcher = new GameMatcher([Lib], [], []);

        Assert.True(matcher.IsGame(Game));
        Assert.False(matcher.IsGame(P(1, "UnityCrashHandler64", $@"{Lib}\Game\UnityCrashHandler64.exe")));
        Assert.False(matcher.IsGame(P(2, "EasyAntiCheat", $@"{Lib}\Game\EasyAntiCheat\EasyAntiCheat.exe")));
        Assert.False(matcher.IsGame(P(3, "chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe")));
        Assert.False(matcher.IsGame(P(4, "NoPath")));
    }

    [Fact]
    public void Matcher_respects_user_lists()
    {
        var matcher = new GameMatcher([Lib], [@"C:\Games\Indie\indie.exe"], ["Game.exe"]);

        Assert.True(matcher.IsGame(P(1, "indie", @"C:\Games\Indie\indie.exe")));
        Assert.False(matcher.IsGame(Game));
    }

    [Fact]
    public void Matcher_picks_largest_game_process()
    {
        var matcher = new GameMatcher([Lib], [], []);
        var small = P(5, "Launcher2", $@"{Lib}\Game\Launcher2.exe", ws: 50);

        Assert.Equal(100, matcher.FindGame(S(small, Game))!.Pid);
    }

    // ---- Aggregator ----

    [Fact]
    public void Aggregator_averages_over_all_samples_and_groups_by_name()
    {
        var agg = new SessionAggregator("Game", Game.Path!, "Game", DateTimeOffset.Now);
        agg.Add(S(Game, P(1, "chrome", cpu: 4), P(2, "chrome", cpu: 2)));
        agg.Add(S(Game)); // chrome gone in the second sample

        var report = agg.Build(DateTimeOffset.Now);

        var chrome = Assert.Single(report.Background);
        Assert.Equal(3, chrome.AvgCpuPercent, 3); // (4+2)/2 samples
        Assert.Equal(6, chrome.MaxCpuPercent, 3);
        Assert.Equal(2, chrome.MaxInstances);
        Assert.Equal("Game", report.Game!.Name);
    }

    [Fact]
    public void Aggregator_reports_cpu_limits()
    {
        var agg = new SessionAggregator("Game", Game.Path!, "Game", DateTimeOffset.Now);
        // "% Performance Limit" is the share of max performance allowed: 100 = unrestricted.
        agg.Add(new SystemSample(DateTimeOffset.Now, new CpuSample(80, 90, 70, 3800), [Game]));
        agg.Add(new SystemSample(DateTimeOffset.Now, new CpuSample(80, 120, 100, 5000), [Game]));
        agg.Add(new SystemSample(DateTimeOffset.Now, new CpuSample(80, 120, 100, 5000, LimitFlags: 2), [Game]));
        agg.Add(new SystemSample(DateTimeOffset.Now, new CpuSample(80, 120, 100, 5000), [Game]));

        var cpu = agg.Build(DateTimeOffset.Now).Cpu!;

        Assert.Equal(50, cpu.LimitedSamplePercent, 3);
        Assert.Equal(3800, cpu.MinEffectiveMhz, 3);
        Assert.Equal(70, cpu.MinAllowedPerformancePercent, 3);
    }

    // ---- Monitor ----

    private (SessionMonitor Monitor, FakeSampler Sampler, FakeProcesses Processes, SessionStore Store, TweakEngine Engine) Create(params string[] efficiencyApps)
    {
        var sampler = new FakeSampler();
        var processes = new FakeProcesses();
        var engine = new TweakEngine(processes, new Journal(Path.Combine(_dir, "journal")), "test");
        var store = new SessionStore(Path.Combine(_dir, "sessions"));
        var settings = new AppSettings { EfficiencyModeApps = efficiencyApps };
        var monitor = new SessionMonitor(sampler, () => new GameMatcher([Lib], [], []), () => settings, engine, store);
        return (monitor, sampler, processes, store, engine);
    }

    [Fact]
    public void Monitor_runs_a_full_session_and_saves_a_report()
    {
        var (monitor, sampler, _, store, _) = Create();
        ActiveSession? started = null;
        SessionReport? ended = null;
        monitor.SessionStarted += s => started = s;
        monitor.SessionEnded += r => ended = r;

        sampler.Next = S(P(1, "chrome", cpu: 5));
        monitor.Tick();
        Assert.Null(monitor.Current);

        sampler.Next = S(Game, P(1, "chrome", cpu: 5));
        monitor.Tick(); // starts
        for (var i = 0; i < 4; i++)
            monitor.Tick(); // first is skipped (rate counters), 3 recorded

        sampler.Next = S(P(1, "chrome", cpu: 5));
        monitor.Tick(); // game gone → ends

        Assert.Equal("Game", started!.GameName);
        Assert.NotNull(ended);
        Assert.Equal(3, ended!.SampleCount);
        Assert.Equal("chrome", Assert.Single(ended.Background).Name);
        Assert.Single(store.LoadAll());
        Assert.Null(monitor.Current);
    }

    [Fact]
    public void Monitor_throttles_only_chosen_apps_and_restores_them_after_the_game()
    {
        var (monitor, sampler, processes, _, engine) = Create("chrome", "explorer");

        sampler.Next = S(Game, P(1, "chrome"), P(2, "explorer"), P(3, "discord"));
        monitor.Tick();

        Assert.Contains(1, processes.Throttled);
        Assert.DoesNotContain(2, processes.Throttled); // protected even if listed
        Assert.DoesNotContain(3, processes.Throttled); // not chosen
        Assert.DoesNotContain(100, processes.Throttled); // never the game
        Assert.Equal(JournalStatus.Pending, Assert.Single(engine.Journal.LoadAll()).Status);

        sampler.Next = S(P(1, "chrome"));
        monitor.Tick();

        Assert.Empty(processes.Throttled);
        Assert.Equal(JournalStatus.Reverted, Assert.Single(engine.Journal.LoadAll()).Status);
    }

    [Fact]
    public void Monitor_throttles_new_instances_started_during_the_session()
    {
        var (monitor, sampler, processes, _, _) = Create("chrome");

        sampler.Next = S(Game);
        monitor.Tick();
        sampler.Next = S(Game, P(7, "chrome"));
        monitor.Tick();

        Assert.Contains(7, processes.Throttled);
    }

    [Fact]
    public void Crash_during_session_is_recovered_on_next_start()
    {
        var (monitor, sampler, processes, _, _) = Create("chrome");
        sampler.Next = S(Game, P(1, "chrome"));
        monitor.Tick();
        Assert.Contains(1, processes.Throttled);

        // App dies here without Stop(). Next start:
        var engine = new TweakEngine(processes, new Journal(Path.Combine(_dir, "journal")), "test");
        engine.RecoverPending();

        Assert.Empty(processes.Throttled);
    }

    [Fact]
    public void Stop_ends_the_session_and_restores_processes()
    {
        var (monitor, sampler, processes, _, _) = Create("chrome");
        sampler.Next = S(Game, P(1, "chrome"));
        monitor.Tick();

        monitor.Stop();

        Assert.Empty(processes.Throttled);
        Assert.Null(monitor.Current);
    }

    private sealed class FakeSampler : ISystemSampler
    {
        public SystemSample Next { get; set; } = S();
        public SystemSample Sample(bool detailed) => Next;
        public void Dispose() { }
    }

    /// <summary>Tracks which PIDs are in efficiency mode.</summary>
    private sealed class FakeProcesses : IChangeExecutor
    {
        private static readonly ProcessEfficiencyState Normal = new(0x20, 0, 0);

        public HashSet<int> Throttled { get; } = [];

        public Change Capture(Change desired) =>
            ((ProcessEfficiencyChange)desired) with { Before = Normal, Captured = true };

        public void Apply(Change change) => Throttled.Add(((ProcessEfficiencyChange)change).Pid);

        public void Revert(Change change) => Throttled.Remove(((ProcessEfficiencyChange)change).Pid);

        public ChangeState Inspect(Change change) =>
            Throttled.Contains(((ProcessEfficiencyChange)change).Pid) ? ChangeState.Applied : ChangeState.Original;
    }
}
