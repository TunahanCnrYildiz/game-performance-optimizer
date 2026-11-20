using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Changes;
using Optimizer.Core.Platform;

namespace Optimizer.App.ViewModels;

/// <summary>
/// Administrator-level settings, whatever the platform offers. Every action: explicit
/// confirmation → (restore point) → elevation prompt → journal.
/// </summary>
public sealed partial class AdvancedViewModel(ChangeService changes, IUserDialogs dialogs, IPlatformServices platform) : ObservableObject
{
    public event Func<Task>? Changed;

    public ObservableCollection<AdvancedGroupViewModel> Groups { get; } = [];

    public bool SupportsRestorePoint => platform.Advanced.SupportsRestorePoint;

    public string ElevationName => platform.Name == "Windows" ? "Windows yönetici izni (UAC)" : "yönetici parolası (pkexec)";

    public string Intro =>
        $"Her işlemden önce ne değişeceği gösterilir ve onayınız alınır; ardından {ElevationName} istenir. Yalnızca bu sayfadaki ayarlar "
        + "değiştirilebilir. Yeniden başlatma gerektirenler, yeniden başlattıktan sonra onaylamazsanız geri alınır. Hepsi Değişiklik geçmişinden geri alınabilir.";

    [ObservableProperty] public partial bool CreateRestorePoint { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }
    [ObservableProperty] public partial string? RestartText { get; set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool NeedsRestart => !string.IsNullOrEmpty(RestartText);

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnRestartTextChanged(string? value) => OnPropertyChanged(nameof(NeedsRestart));

    public void Refresh()
    {
        var journal = changes.Journal.LoadAll();
        var pendingRestart = journal.Where(e => e is { Status: JournalStatus.Pending, RestartRequired: true }).ToList();
        RestartText = pendingRestart.Count == 0
            ? null
            : "Yeniden başlatma bekleyen değişiklikler: " + string.Join(", ", pendingRestart.Select(e => e.Title))
              + ". Yeniden başlattıktan sonra program açılıp onayınızı isteyecek.";

        Groups.Clear();
        try
        {
            foreach (var group in platform.Advanced.GetGroups(journal))
                Groups.Add(new AdvancedGroupViewModel(group, RunAsync));
        }
        catch (Exception ex)
        {
            ShowMessage($"Ayarlar okunamadı: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        var ok = await dialogs.ConfirmAsync(
            "Yeniden başlat",
            "Bilgisayar 10 saniye içinde yeniden başlatılacak. Açık belgelerinizi kaydedin.",
            "Yeniden başlat",
            "Vazgeç");
        if (ok)
            platform.RestartComputer(10);
    }

    private async Task RunAsync(AdvancedAction action)
    {
        if (IsBusy || !action.Enabled)
            return;

        var tweak = action.Tweak;
        var details = action.Explanation + "\n\nYapılacak değişiklikler:\n" + string.Join("\n", action.Changes.Select(c => "• " + c.Describe()))
                      + (tweak.RequiresAdmin ? $"\n\n{char.ToUpper(ElevationName[0])}{ElevationName[1..]} istenecek." : "")
                      + (tweak.RequiresRestart ? " Değişiklik yeniden başlatmadan sonra etkinleşir; ardından onayınız istenir." : "")
                      + "\nDeğişiklik geçmişinden geri alınabilir.";
        if (!await dialogs.ConfirmAsync(tweak.Title, details, action.ExtraConfirm ?? "Uygula", "Vazgeç"))
            return;

        IsBusy = true;
        try
        {
            var useRestorePoint = SupportsRestorePoint && CreateRestorePoint && tweak.RequiresAdmin;
            var outcome = await changes.ApplyAsync(tweak, action.Changes, createRestorePoint: useRestorePoint, requireRestorePoint: useRestorePoint);

            if (!outcome.Result.Success && outcome.RestorePointError is { } restoreError)
            {
                var proceed = await dialogs.ConfirmAsync(
                    "Geri yükleme noktası oluşturulamadı",
                    $"{restoreError}\n\nDeğişiklik yine de bu programın geri alma günlüğüne kaydedilir ve buradan geri alınabilir. Geri yükleme noktası olmadan devam edilsin mi?",
                    "Devam et",
                    "Vazgeç");
                if (!proceed)
                    return;
                outcome = await changes.ApplyAsync(tweak, action.Changes);
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

public sealed class AdvancedGroupViewModel
{
    public AdvancedGroupViewModel(AdvancedGroup group, Func<AdvancedAction, Task> run)
    {
        Group = group;
        Actions = group.Actions.Select(a => new AdvancedActionViewModel(a, run)).ToList();
        SubItems = group.SubItems.Select(s => new AdvancedSubItemViewModel(s, run)).ToList();
    }

    public AdvancedGroup Group { get; }
    public IReadOnlyList<AdvancedActionViewModel> Actions { get; }
    public IReadOnlyList<AdvancedSubItemViewModel> SubItems { get; }

    public string Title => Group.Title;
    public string Description => Group.Description;
    public string State => Group.State;
    public bool HasState => !string.IsNullOrEmpty(Group.State);
    public bool IsSecuritySensitive => Group.IsSecuritySensitive;
    public IReadOnlyList<string> Lines => Group.Lines;
}

public sealed class AdvancedActionViewModel(AdvancedAction action, Func<AdvancedAction, Task> run)
{
    public string Label => action.Label;
    public bool Enabled => action.Enabled;
    public IAsyncRelayCommand Command { get; } = new AsyncRelayCommand(() => run(action));
}

public sealed class AdvancedSubItemViewModel(AdvancedSubItem item, Func<AdvancedAction, Task> run)
{
    public string Name => item.Name;
    public string Detail => item.Detail;
    public string State => item.State;
    public bool HasAction => item.Action is not null;
    public string ActionLabel => item.Action?.Label ?? "";
    public IAsyncRelayCommand Command { get; } = new AsyncRelayCommand(() => item.Action is { } a ? run(a) : Task.CompletedTask);
}
