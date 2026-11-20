using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Changes;
using Optimizer.Core.Inventory;
using Optimizer.Platform.Windows.Changes;
using Optimizer.Platform.Windows.Sessions;

namespace Optimizer.App.ViewModels;

/// <summary>Administrator-level settings. Every action: explicit confirmation → (restore point) → UAC → journal.</summary>
public sealed partial class AdvancedViewModel(ChangeService changes, IUserDialogs dialogs) : ObservableObject
{
    public event Func<Task>? Changed;

    public ObservableCollection<StartupItemViewModel> StartupItems { get; } = [];
    public ObservableCollection<string> GameFolders { get; } = [];

    [ObservableProperty] public partial bool CreateRestorePoint { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    [ObservableProperty] public partial string HagsText { get; set; } = "";
    [ObservableProperty] public partial bool CanEnableHags { get; set; }
    [ObservableProperty] public partial bool CanDisableHags { get; set; }

    [ObservableProperty] public partial string HvciText { get; set; } = "";
    [ObservableProperty] public partial bool CanDisableHvci { get; set; }
    [ObservableProperty] public partial bool CanEnableHvci { get; set; }

    [ObservableProperty] public partial string DefenderText { get; set; } = "";
    [ObservableProperty] public partial bool CanAddExclusions { get; set; }

    [ObservableProperty] public partial string? RestartText { get; set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool NeedsRestart => !string.IsNullOrEmpty(RestartText);

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnRestartTextChanged(string? value) => OnPropertyChanged(nameof(NeedsRestart));

    public void Refresh()
    {
        var pendingRestart = changes.AwaitingRestart();
        RestartText = pendingRestart.Count == 0
            ? null
            : "Yeniden başlatma bekleyen değişiklikler: " + string.Join(", ", pendingRestart.Select(e => e.Title))
              + ". Yeniden başlattıktan sonra program açılıp onayınızı isteyecek.";
        bool Waiting(string tweakId) => pendingRestart.Any(e => e.TweakId == tweakId);

        // HAGS
        var hags = AdvancedSettings.Hags();
        var hagsPending = Waiting(AdvancedSettings.HagsOn.Id) || Waiting(AdvancedSettings.HagsOff.Id);
        HagsText = hags switch
        {
            Toggle.On => "Şu an: açık.",
            Toggle.Off => "Şu an: kapalı.",
            _ => "Şu an: Windows varsayılanı (kayıt defterinde ayar yok).",
        } + (hagsPending ? " Yeniden başlatma bekleyen bir değişiklik var." : "");
        CanEnableHags = !hagsPending && hags != Toggle.On;
        CanDisableHags = !hagsPending && hags != Toggle.Off;

        // HVCI
        try
        {
            var hvci = AdvancedSettings.ReadHvci();
            var hvciPending = Waiting(AdvancedSettings.HvciOff.Id) || Waiting(AdvancedSettings.HvciOn.Id);
            HvciText = (hvci.Running == Toggle.On ? "Şu an: çalışıyor." : "Şu an: çalışmıyor.")
                       + (hvci.Locked ? " UEFI kilidiyle açık tutuluyor; buradan değiştirilemez." : "")
                       + (hvci.PolicyManaged ? " Grup İlkesi tarafından yönetiliyor; buradan değiştirilemez." : "")
                       + (hvciPending ? " Yeniden başlatma bekleyen bir değişiklik var." : "");
            CanDisableHvci = hvci.CanChange && !hvciPending && hvci.Running == Toggle.On;
            CanEnableHvci = hvci.CanChange && !hvciPending && hvci.Running != Toggle.On;
        }
        catch (Exception ex)
        {
            HvciText = $"Durum okunamadı: {ex.Message}";
            CanDisableHvci = CanEnableHvci = false;
        }

        // Defender
        GameFolders.Clear();
        foreach (var folder in GameLibraryLocator.FindGameRoots())
            GameFolders.Add(folder);
        var openExclusion = changes.Journal.LoadAll()
            .FirstOrDefault(e => e.TweakId == AdvancedSettings.DefenderExclusion.Id && e.CanRevert);
        DefenderText = GameFolders.Count == 0
            ? "Oyun kütüphanesi bulunamadı."
            : openExclusion is not null
                ? $"Bu program {openExclusion.CreatedAt:dd.MM.yyyy} tarihinde istisna ekledi. Kaldırmak için Değişiklik geçmişi'nden geri alın."
                : "Şu an bu program tarafından eklenmiş istisna yok. (Mevcut istisnalar yönetici izni olmadan okunamaz.)";
        CanAddExclusions = GameFolders.Count > 0 && openExclusion is null;

        // Startup
        StartupItems.Clear();
        try
        {
            foreach (var item in AdvancedSettings.ReadStartupItems().Where(i => i.ApprovedKey.Length > 0).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                StartupItems.Add(new StartupItemViewModel(item, ToggleStartupAsync));
        }
        catch (Exception ex)
        {
            ShowMessage($"Başlangıç programları okunamadı: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private Task EnableHagsAsync() => RunAsync(
        AdvancedSettings.HagsOn, [AdvancedSettings.HagsChange(true)],
        "Donanım hızlandırmalı GPU zamanlaması açılacak. NVIDIA DLSS Kare Oluşturma için gereklidir; diğer durumlarda FPS'e etkisi genelde ölçülemeyecek kadar küçüktür.");

    [RelayCommand]
    private Task DisableHagsAsync() => RunAsync(
        AdvancedSettings.HagsOff, [AdvancedSettings.HagsChange(false)],
        "Donanım hızlandırmalı GPU zamanlaması kapatılacak. NVIDIA DLSS Kare Oluşturma bu ayar kapalıyken çalışmaz.");

    [RelayCommand]
    private Task DisableHvciAsync() => RunAsync(
        AdvancedSettings.HvciOff, [AdvancedSettings.HvciChange(false)],
        "Bellek Bütünlüğü kapatılacak.\n\n"
        + "Kazanç: İşlemcinin darboğaz olduğu bazı oyunlarda %0–15 arası FPS artışı bildirilmiş; çoğu oyunda fark ölçülemiyor.\n\n"
        + "Bedel: Kötü amaçlı ya da açığı olan sürücülerin çekirdeğe kod yerleştirmesine karşı önemli bir koruma kalkar. "
        + "Bazı anti-cheat sistemleri güvenlik durumunu denetliyor; ileride bazı oyunlar bu ayar kapalıyken açılmayabilir. "
        + "Windows güncellemeleri bu ayarı yeniden açabilir.\n\n"
        + "Önce FPS ölçümü sekmesinde 'önce/sonra' ölçerek gerçekten kazanç olup olmadığını görmeniz önerilir.",
        extraConfirm: "Güvenlik korumasını kapatmayı anlıyorum, devam et");

    [RelayCommand]
    private Task EnableHvciAsync() => RunAsync(
        AdvancedSettings.HvciOn, [AdvancedSettings.HvciChange(true)],
        "Bellek Bütünlüğü açılacak (Microsoft'un önerdiği güvenli durum). Uyumsuz bir sürücü varsa Windows açmayı reddedebilir.");

    [RelayCommand]
    private Task AddExclusionsAsync() => RunAsync(
        AdvancedSettings.DefenderExclusion, AdvancedSettings.DefenderChanges(GameFolders),
        "Şu klasörler Microsoft Defender taramasından hariç tutulacak:\n" + string.Join("\n", GameFolders)
        + "\n\nGerçek zamanlı koruma açık kalır; yalnızca bu klasörlerdeki dosyalar taranmaz. Ortalama FPS'e etkisi çok küçüktür; "
        + "oyun dosya yüklerken oluşan anlık takılmaları azaltabilir. Bedeli: bu klasörlere düşen zararlı bir dosya fark edilmez. "
        + "Yalnızca güvendiğiniz mağazalardan (Steam, Epic) oyun kuruyorsanız kullanın.");

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        var ok = await dialogs.ConfirmAsync(
            "Yeniden başlat",
            "Bilgisayar 10 saniye içinde yeniden başlatılacak. Açık belgelerinizi kaydedin.",
            "Yeniden başlat",
            "Vazgeç");
        if (ok)
            Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10") { CreateNoWindow = true, UseShellExecute = false });
    }

    private Task ToggleStartupAsync(StartupItemViewModel item) => RunAsync(
        AdvancedSettings.StartupToggle(item.Item, enable: !item.Item.Enabled),
        [AdvancedSettings.StartupChange(item.Item, enable: !item.Item.Enabled)],
        item.Item.Enabled
            ? $"\"{item.Item.Name}\" Windows açılışında artık başlamayacak (Görev Yöneticisi'ndeki 'Devre dışı bırak' ile aynı; program silinmez)."
            : $"\"{item.Item.Name}\" Windows açılışında yeniden başlayacak.");

    private async Task RunAsync(ITweak tweak, IReadOnlyList<Change> changeList, string explanation, string? extraConfirm = null)
    {
        if (IsBusy)
            return;

        var details = explanation + "\n\nYapılacak değişiklikler:\n" + string.Join("\n", changeList.Select(c => "• " + c.Describe()))
                      + (tweak.RequiresAdmin ? "\n\nWindows yönetici izni isteyecek." : "")
                      + (tweak.RequiresRestart ? " Değişiklik yeniden başlatmadan sonra etkinleşir; ardından onayınız istenir." : "")
                      + "\nDeğişiklik geçmişinden geri alınabilir.";
        if (!await dialogs.ConfirmAsync(tweak.Title, details, extraConfirm ?? "Uygula", "Vazgeç"))
            return;

        IsBusy = true;
        try
        {
            var useRestorePoint = CreateRestorePoint && tweak.RequiresAdmin;
            var outcome = await changes.ApplyAsync(tweak, changeList, createRestorePoint: useRestorePoint, requireRestorePoint: useRestorePoint);

            if (!outcome.Result.Success && outcome.RestorePointError is { } restoreError)
            {
                var proceed = await dialogs.ConfirmAsync(
                    "Geri yükleme noktası oluşturulamadı",
                    $"{restoreError}\n\nDeğişiklik yine de bu programın geri alma günlüğüne kaydedilir ve buradan geri alınabilir. Geri yükleme noktası olmadan devam edilsin mi?",
                    "Devam et",
                    "Vazgeç");
                if (!proceed)
                    return;
                outcome = await changes.ApplyAsync(tweak, changeList);
            }

            var result = outcome.Result;
            if (!result.Success)
                ShowMessage($"\"{tweak.Title}\" uygulanamadı: {result.Error}", true);
            else if (result.NothingToDo)
                ShowMessage($"\"{tweak.Title}\": ayar zaten istenen durumda.", false);
            else
                ShowMessage(
                    $"\"{tweak.Title}\" uygulandı."
                    + (outcome.RestorePointCreated ? " Önce bir Sistem Geri Yükleme noktası oluşturuldu." : "")
                    + (tweak.RequiresRestart ? " Etkinleşmesi için bilgisayarı yeniden başlatın." : ""),
                    false);
        }
        catch (Exception ex)
        {
            ShowMessage($"Beklenmeyen hata: {ex.Message}", true);
        }
        finally
        {
            IsBusy = false;
            Refresh();
            if (Changed is { } handler)
                await handler();
        }
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }
}

public sealed class StartupItemViewModel
{
    public StartupItemViewModel(StartupItem item, Func<StartupItemViewModel, Task> toggle)
    {
        Item = item;
        ToggleCommand = new AsyncRelayCommand(() => toggle(this));
    }

    public StartupItem Item { get; }
    public IAsyncRelayCommand ToggleCommand { get; }

    public string Name => Item.Name;
    public string Location => Item.Location + (Item.ApprovedHive == "HKLM" ? " · yönetici izni gerekir" : "");
    public string StateText => Item.Enabled ? "Açık" : "Devre dışı";
    public string ToggleText => Item.Enabled ? "Devre dışı bırak" : "Etkinleştir";
}
