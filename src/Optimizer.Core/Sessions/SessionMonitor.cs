using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Inventory;

namespace Optimizer.Core.Sessions;

public sealed record ActiveSession(string GameName, string GamePath, int GamePid, DateTimeOffset StartedAt);

/// <summary>
/// Watches for a game, measures what else uses the machine while it runs, and — only
/// for apps the user picked — applies efficiency mode for the duration of the game.
/// Session changes are journaled as Pending, so a crash is undone on next start.
/// </summary>
public sealed class SessionMonitor : IDisposable
{
    /// <summary>Never throttled, whatever the user list says: the OS shell, audio, input and anti-cheat.</summary>
    internal static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "fontdrvhost", "sihost", "ctfmon", "audiodg", "conhost", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "TextInputHost", "MsMpEng", "SecurityHealthService", "vgc", "vgtray", "EasyAntiCheat", "EasyAntiCheat_EOS",
        "BEService", "FACEIT", "faceitservice", "PerformanceOptimizer", "optimizer",
    };

    public static bool IsProtected(string processName) => Protected.Contains(NormalizeName(processName));

    /// <summary>"Discord.exe" → "Discord", the form process names are compared in.</summary>
    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private readonly ISystemSampler _sampler;
    private readonly Func<GameMatcher> _matcherFactory;
    private readonly Func<AppSettings> _settings;
    private readonly TweakEngine _engine;
    private readonly SessionStore _store;
    private readonly TimeProvider _clock;
    private readonly Lock _lock = new();

    private GameMatcher? _matcher;
    private SessionAggregator? _aggregator;
    private bool _skipNextSample;
    private readonly List<Guid> _sessionEntries = [];
    private readonly HashSet<(int Pid, DateTime Start)> _throttled = [];
    private readonly HashSet<string> _throttledNames = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loop;

    public SessionMonitor(
        ISystemSampler sampler,
        Func<GameMatcher> matcherFactory,
        Func<AppSettings> settings,
        TweakEngine engine,
        SessionStore store,
        TimeProvider? clock = null)
    {
        _sampler = sampler;
        _matcherFactory = matcherFactory;
        _settings = settings;
        _engine = engine;
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    public ActiveSession? Current { get; private set; }

    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan SessionInterval { get; init; } = TimeSpan.FromSeconds(2);

    public event Action<ActiveSession>? SessionStarted;
    public event Action<SessionReport>? SessionEnded;
    public event Action<string>? Error;

    /// <summary>Re-reads game libraries and user lists on the next idle tick.</summary>
    public void InvalidateMatcher()
    {
        lock (_lock)
            _matcher = null;
    }

    public void Start()
    {
        if (_loop is not null)
            return;

        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    Tick();
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex.Message);
                }

                try
                {
                    await Task.Delay(Current is null ? IdleInterval : SessionInterval, _clock, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    /// <summary>Stops watching; an active session is ended and its changes are reverted.</summary>
    public void Stop()
    {
        _loop?.Cancel();
        _loop = null;
        lock (_lock)
        {
            if (Current is not null)
                EndSession();
        }
    }

    /// <summary>One step of the loop. Public so it can be driven directly in tests.</summary>
    public void Tick()
    {
        lock (_lock)
        {
            if (Current is null)
            {
                var sample = _sampler.Sample(detailed: false);
                _matcher ??= _matcherFactory();
                if (_matcher.FindGame(sample) is { } game)
                    BeginSession(game, sample);
                return;
            }

            var detailed = _sampler.Sample(detailed: true);
            if (!detailed.Processes.Any(p => p.Pid == Current.GamePid))
            {
                EndSession();
                return;
            }

            // Rate counters (CPU %, GPU %, I/O) need two readings; the first one after start is not meaningful.
            if (_skipNextSample)
                _skipNextSample = false;
            else
                _aggregator!.Add(detailed);

            ApplyEfficiency(detailed);
        }
    }

    private void BeginSession(ProcessSample game, SystemSample sample)
    {
        // Split on both separators: a Proton game on Linux can still report a Windows-style path.
        var file = game.Path!.Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(file[(file.LastIndexOf('/') + 1)..]) is { Length: > 0 } n ? n : game.Name;
        Current = new ActiveSession(name, game.Path!, game.Pid, _clock.GetLocalNow());
        _aggregator = new SessionAggregator(name, game.Path!, game.Name, Current.StartedAt);
        _skipNextSample = true;
        _sessionEntries.Clear();
        _throttled.Clear();
        _throttledNames.Clear();

        ApplyEfficiency(sample);
        SessionStarted?.Invoke(Current);
    }

    private void EndSession()
    {
        var session = Current!;
        foreach (var id in _sessionEntries)
        {
            var result = _engine.Revert(id);
            if (!result.Success)
                Error?.Invoke($"Oturum değişikliği geri alınamadı: {result.Error}");
        }

        var report = _aggregator!.Build(_clock.GetLocalNow(), _throttledNames.Order(StringComparer.OrdinalIgnoreCase).ToList());
        Current = null;
        _aggregator = null;
        _sessionEntries.Clear();

        // Very short "sessions" are usually a launcher or a game that failed to start.
        if (report.SampleCount >= 3)
        {
            _store.Save(report);
            SessionEnded?.Invoke(report);
        }
    }

    private void ApplyEfficiency(SystemSample sample)
    {
        var wanted = new HashSet<string>(_settings().EfficiencyModeApps.Select(NormalizeName), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0 || Current is null)
            return;

        var targets = sample.Processes
            .Where(p => p.Pid != Current.GamePid
                        && wanted.Contains(p.Name)
                        && !Protected.Contains(p.Name)
                        && !_throttled.Contains((p.Pid, p.StartTimeUtc))
                        && !(_matcher?.IsGame(p) ?? false))
            .ToList();
        if (targets.Count == 0)
            return;

        foreach (var p in targets)
            _throttled.Add((p.Pid, p.StartTimeUtc));

        var changes = targets.Select(p => (Change)new ProcessEfficiencyChange(p.Pid, p.Name, p.StartTimeUtc, null)).ToList();

        // Pending on purpose: if the app dies mid-game, startup recovery restores these processes.
        var result = _engine.Apply(new SessionTweak(), changes, keepPending: true);
        if (result.Entry is { } entry && result.Success)
        {
            _sessionEntries.Add(entry.Id);
            foreach (var p in targets)
                _throttledNames.Add(p.Name);
        }
        else if (!result.Success)
        {
            Error?.Invoke($"Verimlilik modu uygulanamadı: {result.Error}");
        }
    }

    public void Dispose()
    {
        Stop();
        _sampler.Dispose();
    }

    private sealed class SessionTweak : ITweak
    {
        public string Id => "session.efficiency-mode";
        public string Title => "Oyun sırasında verimlilik modu";
        public Risk Risk => Risk.Low;
        public bool Handles(Finding finding) => false;
        public IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) => [];
    }
}
