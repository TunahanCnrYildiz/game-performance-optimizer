using System.Diagnostics;
using Optimizer.Core.Benchmarks;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Diagnostics.Rules;
using Optimizer.Core.Elevation;
using Optimizer.Core.Inventory;
using Optimizer.Core.Platform;
using Optimizer.Core.Sessions;

namespace Optimizer.Platform.Linux;

public sealed class LinuxPlatform : IPlatformServices
{
    public static readonly ManualTweak MaxMapCount = new("linux.max-map-count.raise", "vm.max_map_count değerini 1048576 yap", Risk.Low, RequiresAdmin: true);
    public static readonly ManualTweak SplitLockOff = new("linux.split-lock.off", "Split-lock yavaşlatmasını kapat", Risk.Medium, RequiresAdmin: true);
    public static readonly ManualTweak SplitLockOn = new("linux.split-lock.on", "Split-lock yavaşlatmasını aç (varsayılan)", Risk.Low, RequiresAdmin: true);

    public LinuxPlatform()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(Home, ".local", "share");
        Paths = new PlatformPaths(Path.Combine(dataHome, "PerformanceOptimizer"));
        Elevation = new PkexecRunner(Path.Combine(Paths.Root, "elevated"));
        Advanced = new LinuxAdvancedProvider(new PowerProfilesCtl());
    }

    private static string Home => Environment.GetEnvironmentVariable("HOME") ?? "/root";

    public string Name => "Linux";
    public PlatformPaths Paths { get; }
    public IInventoryCollector Inventory { get; } = new LinuxInventoryCollector();
    public ITweakCatalog Tweaks { get; } = new LinuxTweakCatalog();
    public IChangeExecutor Executor { get; } = new LinuxChangeExecutor();
    public IFrameCapture FrameCapture { get; } = new MangoHudLog();
    public IElevatedRunner Elevation { get; }
    public IAdvancedProvider Advanced { get; }

    /// <summary>
    /// An unprivileged process may lower another process's priority but not raise it again
    /// (RLIMIT_NICE), so a throttled app could not be restored after the game.
    /// </summary>
    public bool SupportsEfficiencyMode => false;

    public DiagnosticEngine CreateDiagnostics() => DiagnosticEngine.CreateForLinux();

    public ISystemSampler CreateSampler() => new LinuxSystemSampler();

    public IReadOnlyList<string> FindGameRoots() => SteamLibraries.FindGameRoots(LinuxFs.System, Home);

    public int RunElevatedHelper(string requestPath, string responsePath) => LinuxElevatedHelper.Run(requestPath, responsePath);

    /// <summary>XDG autostart entry, removed again once nothing waits for a restart.</summary>
    public void UpdateRestartHook(bool needed)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Home, ".config");
        var file = Path.Combine(configHome, "autostart", "performance-optimizer-after-restart.desktop");
        try
        {
            if (needed && Environment.ProcessPath is { } exe)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file,
                    "[Desktop Entry]\nType=Application\nName=Performance Optimizer (yeniden başlatma onayı)\n"
                    + $"Exec=\"{exe}\" --after-restart\nX-GNOME-Autostart-enabled=true\n");
            }
            else if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void RestartComputer(int delaySeconds) =>
        _ = Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ContinueWith(_ =>
            Process.Start(new ProcessStartInfo("systemctl", "reboot") { UseShellExecute = false }));
}

public sealed class LinuxTweakCatalog : ITweakCatalog
{
    private readonly IReadOnlyList<ITweak> _tweaks = [new MaxMapCountTweak(), new PowerSaverProfileTweak()];

    public ITweak? FindFor(Finding finding) =>
        finding.Severity > Severity.Ok ? _tweaks.FirstOrDefault(t => t.Handles(finding)) : null;

    private sealed class MaxMapCountTweak : ITweak
    {
        public string Id => LinuxPlatform.MaxMapCount.Id;
        public string Title => LinuxPlatform.MaxMapCount.Title;
        public Risk Risk => Risk.Low;
        public bool RequiresAdmin => true;
        public bool Handles(Finding finding) => finding.Id == "linux.max-map-count";

        public IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) =>
            [new SysctlChange("vm.max_map_count", null, LinuxMaxMapCountRule.Recommended.ToString(System.Globalization.CultureInfo.InvariantCulture))];
    }

    private sealed class PowerSaverProfileTweak : ITweak
    {
        public string Id => "linux.power-profile.balanced";
        public string Title => "Dengeli güç profiline geç";
        public Risk Risk => Risk.Low;
        public bool Handles(Finding finding) => finding.Id == "linux.power-profile";
        public IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) => [new PowerProfileChange(null, "balanced")];
    }
}

public sealed class LinuxAdvancedProvider(IPowerProfiles profiles, LinuxFs? fs = null) : IAdvancedProvider
{
    private readonly LinuxFs _fs = fs ?? LinuxFs.System;

    public bool SupportsRestorePoint => false;

    public IReadOnlyList<AdvancedGroup> GetGroups(IReadOnlyList<JournalEntry> journal)
    {
        var groups = new List<AdvancedGroup>();

        var mapCount = _fs.ReadLong("/proc/sys/vm/max_map_count");
        groups.Add(new AdvancedGroup(
            "vm.max_map_count",
            "Bir programın kullanabileceği bellek eşlemesi sayısı. Çekirdek varsayılanı 65530; bazı yeni oyunlar bunu aşınca çöküyor. "
            + "Fedora, Ubuntu ve Arch varsayılanı 1048576'ya yükseltti. Değer /etc/sysctl.d/99-performance-optimizer.conf dosyasına yazılır.",
            mapCount is null ? "Şu an: okunamadı." : $"Şu an: {mapCount}.",
            false,
            [
                new AdvancedAction("1048576 yap", LinuxPlatform.MaxMapCount,
                    [new SysctlChange("vm.max_map_count", null, "1048576")],
                    "vm.max_map_count 1048576 olarak ayarlanacak ve yeniden başlatmalarda korunacak.",
                    Enabled: mapCount is < LinuxMaxMapCountRule.Recommended),
            ],
            [], []));

        var splitLock = _fs.ReadLong("/proc/sys/kernel/split_lock_mitigate");
        if (splitLock is not null)
        {
            groups.Add(new AdvancedGroup(
                "Split-lock yavaşlatması (kernel.split_lock_mitigate)",
                "Çekirdek 6.2'den beri belleğe hizasız kilitli erişim yapan programlar kasıtlı olarak yavaşlatılıyor; bunu yapan birkaç oyun takılıyor. "
                + "SteamOS bu ayarı kapalı getiriyor. Kapatmak, kötü niyetli bir programın sistemi bu yolla yavaşlatmasına karşı korumayı kaldırır.",
                splitLock == 1 ? "Şu an: açık (çekirdek varsayılanı)." : "Şu an: kapalı.",
                true,
                [
                    new AdvancedAction("Kapat", SplitLockOffTweak, [new SysctlChange("kernel.split_lock_mitigate", null, "0")],
                        "Split-lock yavaşlatması kapatılacak. Yalnızca bir oyunda açıklanamayan takılma görüyorsanız ve FPS ölçümüyle fark doğruladıysanız kullanın.",
                        Enabled: splitLock == 1),
                    new AdvancedAction("Aç (varsayılan)", LinuxPlatform.SplitLockOn, [new SysctlChange("kernel.split_lock_mitigate", null, "1")],
                        "Split-lock yavaşlatması yeniden açılacak (çekirdeğin varsayılan, güvenli durumu).",
                        Enabled: splitLock == 0),
                ],
                [], []));
        }

        var current = profiles.Get();
        var available = profiles.Available();
        if (available.Count > 0)
        {
            groups.Add(new AdvancedGroup(
                "Güç profili (power-profiles-daemon)",
                "İşlemcinin (ve bazı sistemlerde ekran kartının) güç sınırlarını belirler. Yönetici izni gerekmez.",
                $"Şu an: {current ?? "?"}.",
                false,
                available.Select(p => new AdvancedAction(
                    p switch { "power-saver" => "Güç tasarrufu", "balanced" => "Dengeli", "performance" => "Performans", _ => p },
                    new ManualTweak($"linux.power-profile.{p}", $"Güç profili: {p}", Risk.Low),
                    [new PowerProfileChange(null, p)],
                    $"Güç profili '{p}' olarak ayarlanacak.",
                    Enabled: p != current)).ToList(),
                [], []));
        }

        return groups;
    }

    private static ManualTweak SplitLockOffTweak => LinuxPlatform.SplitLockOff;
}
