using System.Diagnostics;
using Microsoft.Win32;
using Optimizer.Core.Benchmarks;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Elevation;
using Optimizer.Core.Inventory;
using Optimizer.Core.Platform;
using Optimizer.Core.Sessions;
using Optimizer.Platform.Windows.Benchmarks;
using Optimizer.Platform.Windows.Changes;
using Optimizer.Platform.Windows.Elevation;
using Optimizer.Platform.Windows.Sessions;

namespace Optimizer.Platform.Windows;

public sealed class WindowsPlatform : IPlatformServices
{
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceName = "PerformanceOptimizerAfterRestart";

    public string Name => "Windows";
    public PlatformPaths Paths { get; } = new(WindowsAppPaths.Root);
    public IInventoryCollector Inventory { get; } = new WindowsInventoryCollector();
    public ITweakCatalog Tweaks { get; } = new WindowsTweakCatalog();
    public IChangeExecutor Executor { get; } = new WindowsChangeExecutor();
    public IFrameCapture FrameCapture { get; } = (IFrameCapture?)PresentMonRunner.Locate() ?? new MissingPresentMon();
    public IElevatedRunner Elevation { get; } = ElevatedRunner.ForCurrentProcess();
    public IAdvancedProvider Advanced { get; } = new WindowsAdvancedProvider();
    public bool SupportsEfficiencyMode => true;

    public DiagnosticEngine CreateDiagnostics() => DiagnosticEngine.CreateDefault();

    public ISystemSampler CreateSampler() => new WindowsSystemSampler();

    public IReadOnlyList<string> FindGameRoots() => GameLibraryLocator.FindGameRoots();

    public int RunElevatedHelper(string requestPath, string responsePath) => WindowsElevatedHelper.Run(requestPath, responsePath);

    public void UpdateRestartHook(bool needed)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey, writable: true);
            if (needed && Environment.ProcessPath is { } exe)
                key.SetValue(RunOnceName, $"\"{exe}\" --after-restart");
            else
                key.DeleteValue(RunOnceName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Not critical: the user can still open the app manually after restarting.
        }
    }

    public void RestartComputer(int delaySeconds) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", $"/r /t {delaySeconds}") { CreateNoWindow = true, UseShellExecute = false });

    private sealed class MissingPresentMon : IFrameCapture
    {
        public bool CanAttach => false;
        public bool CanImport => false;
        public string? UnavailableReason => @"Ölçüm aracı (Intel PresentMon) bulunamadı. Uygulamanın yanındaki tools klasöründe PresentMon.exe olmalı.";
        public string? ImportHint => null;
        public IReadOnlyList<string> ImportPatterns => [];

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CaptureResult(false, [], UnavailableReason));

        public (string? GameName, IReadOnlyList<FrameRecord> Frames) Import(string path) => throw new NotSupportedException();
    }
}

/// <summary>HAGS, Memory Integrity, Defender game-folder exclusion and startup apps.</summary>
public sealed class WindowsAdvancedProvider : IAdvancedProvider
{
    public bool SupportsRestorePoint => true;

    public IReadOnlyList<AdvancedGroup> GetGroups(IReadOnlyList<JournalEntry> journal)
    {
        var waiting = journal.Where(e => e is { Status: JournalStatus.Pending, RestartRequired: true }).Select(e => e.TweakId).ToHashSet();
        const string pendingNote = " Yeniden başlatma bekleyen bir değişiklik var.";
        var groups = new List<AdvancedGroup>();

        // HAGS
        var hags = AdvancedSettings.Hags();
        var hagsPending = waiting.Contains(AdvancedSettings.HagsOn.Id) || waiting.Contains(AdvancedSettings.HagsOff.Id);
        groups.Add(new AdvancedGroup(
            "Donanım hızlandırmalı GPU zamanlaması (HAGS)",
            "NVIDIA DLSS Kare Oluşturma için gereklidir. Testlerde FPS'e etkisi genelde ölçülemeyecek kadar küçük; emin değilseniz FPS ölçümü sekmesiyle önce/sonra karşılaştırın. Yeniden başlatma gerekir.",
            hags switch
            {
                Toggle.On => "Şu an: açık.",
                Toggle.Off => "Şu an: kapalı.",
                _ => "Şu an: Windows varsayılanı (kayıt defterinde ayar yok).",
            } + (hagsPending ? pendingNote : ""),
            false,
            [
                new AdvancedAction("Aç", AdvancedSettings.HagsOn, [AdvancedSettings.HagsChange(true)],
                    "Donanım hızlandırmalı GPU zamanlaması açılacak. NVIDIA DLSS Kare Oluşturma için gereklidir; diğer durumlarda FPS'e etkisi genelde ölçülemeyecek kadar küçüktür.",
                    Enabled: !hagsPending && hags != Toggle.On),
                new AdvancedAction("Kapat", AdvancedSettings.HagsOff, [AdvancedSettings.HagsChange(false)],
                    "Donanım hızlandırmalı GPU zamanlaması kapatılacak. NVIDIA DLSS Kare Oluşturma bu ayar kapalıyken çalışmaz.",
                    Enabled: !hagsPending && hags != Toggle.Off),
            ],
            [], []));

        // HVCI
        string hvciState;
        bool canOff = false, canOn = false;
        try
        {
            var hvci = AdvancedSettings.ReadHvci();
            var pending = waiting.Contains(AdvancedSettings.HvciOff.Id) || waiting.Contains(AdvancedSettings.HvciOn.Id);
            hvciState = (hvci.Running == Toggle.On ? "Şu an: çalışıyor." : "Şu an: çalışmıyor.")
                        + (hvci.Locked ? " UEFI kilidiyle açık tutuluyor; buradan değiştirilemez." : "")
                        + (hvci.PolicyManaged ? " Grup İlkesi tarafından yönetiliyor; buradan değiştirilemez." : "")
                        + (pending ? pendingNote : "");
            canOff = hvci.CanChange && !pending && hvci.Running == Toggle.On;
            canOn = hvci.CanChange && !pending && hvci.Running != Toggle.On;
        }
        catch (Exception ex)
        {
            hvciState = $"Durum okunamadı: {ex.Message}";
        }

        groups.Add(new AdvancedGroup(
            "Bellek Bütünlüğü (HVCI) — güvenlik ayarı",
            "Kapatmak bazı işlemci darboğazlı oyunlarda %0–15 FPS kazandırabilir, ama sürücü kaynaklı saldırılara karşı önemli bir korumayı kaldırır ve bazı anti-cheat sistemleriyle sorun çıkarabilir. Yeniden başlatma gerekir.",
            hvciState,
            true,
            [
                new AdvancedAction("Kapat", AdvancedSettings.HvciOff, [AdvancedSettings.HvciChange(false)],
                    "Bellek Bütünlüğü kapatılacak.\n\n"
                    + "Kazanç: İşlemcinin darboğaz olduğu bazı oyunlarda %0–15 arası FPS artışı bildirilmiş; çoğu oyunda fark ölçülemiyor.\n\n"
                    + "Bedel: Kötü amaçlı ya da açığı olan sürücülerin çekirdeğe kod yerleştirmesine karşı önemli bir koruma kalkar. "
                    + "Bazı anti-cheat sistemleri güvenlik durumunu denetliyor; ileride bazı oyunlar bu ayar kapalıyken açılmayabilir. "
                    + "Windows güncellemeleri bu ayarı yeniden açabilir.\n\n"
                    + "Önce FPS ölçümü sekmesinde 'önce/sonra' ölçerek gerçekten kazanç olup olmadığını görmeniz önerilir.",
                    Enabled: canOff,
                    ExtraConfirm: "Güvenlik korumasını kapatmayı anlıyorum, devam et"),
                new AdvancedAction("Aç (önerilen)", AdvancedSettings.HvciOn, [AdvancedSettings.HvciChange(true)],
                    "Bellek Bütünlüğü açılacak (Microsoft'un önerdiği güvenli durum). Uyumsuz bir sürücü varsa Windows açmayı reddedebilir.",
                    Enabled: canOn),
            ],
            [], []));

        // Defender
        var folders = GameLibraryLocator.FindGameRoots();
        var open = journal.FirstOrDefault(e => e.TweakId == AdvancedSettings.DefenderExclusion.Id && e.CanRevert);
        groups.Add(new AdvancedGroup(
            "Microsoft Defender: oyun klasörü istisnası",
            "Gerçek zamanlı koruma açık kalır; yalnızca oyun kütüphanesi klasörleri taranmaz. Ortalama FPS'e etkisi çok küçük, yükleme sırasındaki anlık takılmaları azaltabilir. Bedeli: bu klasörlere düşen zararlı bir dosya fark edilmez.",
            folders.Count == 0
                ? "Oyun kütüphanesi bulunamadı."
                : open is not null
                    ? $"Bu program {open.CreatedAt:dd.MM.yyyy} tarihinde istisna ekledi. Kaldırmak için Değişiklik geçmişi'nden geri alın."
                    : "Şu an bu program tarafından eklenmiş istisna yok. (Mevcut istisnalar yönetici izni olmadan okunamaz.)",
            false,
            [
                new AdvancedAction("Bu klasörleri taramadan hariç tut", AdvancedSettings.DefenderExclusion, AdvancedSettings.DefenderChanges(folders),
                    "Şu klasörler Microsoft Defender taramasından hariç tutulacak:\n" + string.Join("\n", folders)
                    + "\n\nGerçek zamanlı koruma açık kalır; yalnızca bu klasörlerdeki dosyalar taranmaz. Ortalama FPS'e etkisi çok küçüktür; "
                    + "oyun dosya yüklerken oluşan anlık takılmaları azaltabilir. Bedeli: bu klasörlere düşen zararlı bir dosya fark edilmez. "
                    + "Yalnızca güvendiğiniz mağazalardan (Steam, Epic) oyun kuruyorsanız kullanın.",
                    Enabled: folders.Count > 0 && open is null),
            ],
            [],
            folders));

        // Startup apps
        var items = new List<AdvancedSubItem>();
        foreach (var item in AdvancedSettings.ReadStartupItems().Where(i => i.ApprovedKey.Length > 0).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var enable = !item.Enabled;
            items.Add(new AdvancedSubItem(
                item.Name,
                item.Location + (item.ApprovedHive == "HKLM" ? " · yönetici izni gerekir" : ""),
                item.Enabled ? "Açık" : "Devre dışı",
                new AdvancedAction(
                    enable ? "Etkinleştir" : "Devre dışı bırak",
                    AdvancedSettings.StartupToggle(item, enable),
                    [AdvancedSettings.StartupChange(item, enable)],
                    enable
                        ? $"\"{item.Name}\" Windows açılışında yeniden başlayacak."
                        : $"\"{item.Name}\" Windows açılışında artık başlamayacak (Görev Yöneticisi'ndeki 'Devre dışı bırak' ile aynı; program silinmez).")));
        }

        groups.Add(new AdvancedGroup(
            "Başlangıç programları",
            "Görev Yöneticisi'ndeki 'Başlangıç uygulamaları' ile aynı mekanizma: program silinmez, yalnızca Windows açılışında başlamaz. Tüm kullanıcılar için kayıtlı olanlar yönetici izni ister.",
            items.Count == 0 ? "Başlangıç programı bulunamadı." : "",
            false,
            [],
            items,
            []));

        return groups;
    }
}
