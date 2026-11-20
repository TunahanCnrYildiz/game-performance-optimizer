using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Inventory;

namespace Optimizer.App.ViewModels;

public sealed record SummaryRow(string Label, string Value);

public sealed partial class MainWindowViewModel(
    IInventoryCollector collector,
    DiagnosticEngine diagnostics,
    ITweakCatalog catalog,
    TweakEngine tweaks,
    IUserDialogs dialogs,
    SessionsViewModel sessions,
    BenchmarksViewModel benchmarks) : ObservableObject
{
    public SessionsViewModel Sessions { get; } = sessions;
    public BenchmarksViewModel Benchmarks { get; } = benchmarks;

    private const double GiB = 1024d * 1024 * 1024;
    private const int ConfirmSeconds = 15;

    private IReadOnlyList<FindingViewModel> _allFindings = [];

    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<SummaryRow> Summary { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];
    public ObservableCollection<HistoryEntryViewModel> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertAllCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool ShowPassed { get; set; } = true;

    [ObservableProperty] public partial int WarningCount { get; set; }
    [ObservableProperty] public partial int SuggestionCount { get; set; }
    [ObservableProperty] public partial int InfoCount { get; set; }
    [ObservableProperty] public partial int OkCount { get; set; }
    [ObservableProperty] public partial int FixableCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial DateTimeOffset? LastScan { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasErrors => Errors.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasOpenChanges => History.Any(h => h.CanRevert);

    public string StatusText => IsBusy
        ? "Çalışıyor…"
        : LastScan is { } t
            ? $"Son tarama: {t:HH:mm:ss}"
            : "Henüz tarama yapılmadı";

    /// <summary>Startup: undo anything a previous run left unconfirmed, then scan.</summary>
    public async Task InitializeAsync()
    {
        var recovered = await Task.Run(tweaks.RecoverPending);
        if (recovered.Count > 0)
        {
            ShowMessage(
                $"Önceki oturumda onaylanmamış {recovered.Count} değişiklik bulundu ve güvenlik için geri alındı. "
                + "Ayrıntılar Değişiklik geçmişi sekmesinde.",
                isError: recovered.Any(r => !r.Success));
        }

        LoadHistory();
        Sessions.Activate();
        await ScanAsync();
    }

    partial void OnShowPassedChanged(bool value) => ApplyFilter();

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            await ScanCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RevertAllAsync()
    {
        var open = History.Count(h => h.CanRevert);
        if (open == 0)
            return;

        var ok = await dialogs.ConfirmAsync(
            "Tüm değişiklikleri geri al",
            $"Bu uygulamanın yaptığı {open} değişikliğin tamamı eski değerlerine döndürülecek. Devam edilsin mi?",
            "Hepsini geri al",
            "Vazgeç");
        if (!ok)
            return;

        await RunBusyAsync(async () =>
        {
            var results = await Task.Run(tweaks.RevertAll);
            var failed = results.Count(r => !r.Success);
            ShowMessage(
                failed == 0
                    ? $"{results.Count} değişiklik geri alındı."
                    : $"{results.Count - failed} değişiklik geri alındı, {failed} tanesinde sorun oluştu. Ayrıntılar geçmişte.",
                failed > 0);
        });
    }

    private async Task ApplyAsync(FindingViewModel finding)
    {
        if (IsBusy || finding.Tweak is not { } tweak)
            return;

        await RunBusyAsync(async () =>
        {
            var result = await Task.Run(() => tweaks.Apply(tweak, finding.Changes, keepPending: tweak.RequiresConfirmation));

            if (!result.Success)
            {
                ShowMessage($"\"{tweak.Title}\" uygulanamadı: {result.Error}", isError: true);
                return;
            }

            if (result.NothingToDo)
            {
                ShowMessage($"\"{tweak.Title}\": ayar zaten istenen durumda, değişiklik yapılmadı.", isError: false);
                return;
            }

            if (tweak.RequiresConfirmation)
            {
                var keep = await dialogs.ConfirmWithTimeoutAsync(
                    "Değişikliği onaylayın",
                    "Ekran yeni ayarla düzgün görünüyor mu? Görüntü bozuksa ya da ekran kararmışsa hiçbir şeye dokunmayın; "
                    + "ayar kendiliğinden geri alınacak.",
                    ConfirmSeconds);

                if (!keep)
                {
                    var reverted = await Task.Run(() => tweaks.Revert(result.Entry!.Id));
                    ShowMessage(
                        reverted.Success ? "Değişiklik onaylanmadı ve geri alındı." : $"Geri alınırken sorun oluştu: {reverted.Error}",
                        isError: !reverted.Success);
                    return;
                }

                tweaks.Confirm(result.Entry!.Id);
            }

            ShowMessage($"\"{tweak.Title}\" uygulandı. Değişiklik geçmişi sekmesinden geri alabilirsiniz.", isError: false);
        });
    }

    private async Task RevertAsync(HistoryEntryViewModel entry)
    {
        if (IsBusy)
            return;

        await RunBusyAsync(async () =>
        {
            var result = await Task.Run(() => tweaks.Revert(entry.Entry.Id));
            if (!result.Success)
                ShowMessage($"Geri alınamadı: {result.Error}", isError: true);
            else if (result.Entry?.Status == JournalStatus.PartiallyReverted)
                ShowMessage("Kısmen geri alındı: bazı değerler sonradan başka bir şey tarafından değiştirildiği için olduğu gibi bırakıldı.", isError: false);
            else
                ShowMessage($"\"{entry.Title}\" geri alındı.", isError: false);
        });
    }

    /// <summary>Runs an action that changes the system, then refreshes history and findings.</summary>
    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowMessage($"Beklenmeyen hata: {ex.Message}", isError: true);
        }
        finally
        {
            LoadHistory();
            await ScanCoreAsync();
            IsBusy = false;
        }
    }

    private void ShowMessage(string text, bool isError)
    {
        MessageIsError = isError;
        Message = text;
    }

    private void LoadHistory()
    {
        History.Clear();
        // Finished game-session entries (efficiency mode, already restored) would flood the list.
        foreach (var entry in tweaks.Journal.LoadAll().Where(e => e.TweakId != "session.efficiency-mode" || e.CanRevert))
            History.Add(new HistoryEntryViewModel(entry, RevertAsync));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasOpenChanges));
    }

    private async Task ScanCoreAsync()
    {
        try
        {
            var inventory = await collector.CollectAsync();
            var findings = await Task.Run(() => diagnostics.Evaluate(inventory));

            _allFindings = findings.Select(f =>
            {
                var tweak = catalog.FindFor(f);
                IReadOnlyList<Change> changes = [];
                try
                {
                    changes = tweak?.GetChanges(inventory, f) ?? [];
                }
                catch (Exception)
                {
                    // A tweak that cannot plan itself is simply not offered.
                }

                return new FindingViewModel(f, tweak, changes, ApplyAsync);
            }).ToList();

            WarningCount = findings.Count(f => f.Severity == Severity.Warning);
            SuggestionCount = findings.Count(f => f.Severity == Severity.Suggestion);
            InfoCount = findings.Count(f => f.Severity == Severity.Info);
            OkCount = findings.Count(f => f.Severity == Severity.Ok);
            FixableCount = _allFindings.Count(f => f.CanFix);

            FillSummary(inventory);
            Errors.Clear();
            foreach (var e in inventory.Errors)
                Errors.Add($"{e.Collector}: {e.Message}");
            OnPropertyChanged(nameof(HasErrors));

            ApplyFilter();
            LastScan = inventory.CollectedAt;
        }
        catch (Exception ex)
        {
            Errors.Clear();
            Errors.Add($"Tarama başarısız oldu: {ex.Message}");
            OnPropertyChanged(nameof(HasErrors));
        }
    }

    private void ApplyFilter()
    {
        Findings.Clear();
        foreach (var f in _allFindings.Where(f => ShowPassed || f.Model.Severity > Severity.Ok))
            Findings.Add(f);
    }

    private void FillSummary(SystemInventory inv)
    {
        Summary.Clear();

        if (inv.Os is { } os)
            Summary.Add(new("Sistem", $"{os.Name}\nSürüm {os.Version}"));

        if (inv.Cpu is { } cpu)
            Summary.Add(new("İşlemci", $"{cpu.Name}\n{cpu.PhysicalCores} çekirdek / {cpu.LogicalProcessors} iş parçacığı"));

        foreach (var gpu in inv.Gpus.Where(g => g.Kind != GpuKind.Virtual))
        {
            var kind = gpu.Kind switch
            {
                GpuKind.Discrete => "harici",
                GpuKind.Integrated => "dahili",
                _ => "bilinmiyor",
            };
            var mem = gpu.DedicatedMemoryBytes is { } m && gpu.Kind == GpuKind.Discrete ? $", {m / GiB:0} GB" : "";
            Summary.Add(new("Ekran kartı", $"{gpu.Name} ({kind}{mem})\nSürücü {gpu.DriverVersion}"));
        }

        if (inv.Memory is { } memory)
        {
            var gen = memory.Generation == MemoryGeneration.Unknown ? "" : $" {memory.Generation.ToString().ToUpperInvariant()}";
            Summary.Add(new("Bellek", $"{memory.TotalBytes / GiB:0} GB{gen}, {memory.Modules.Count} modül\n{memory.ConfiguredSpeedMts} MT/s"));
        }

        foreach (var d in inv.Displays)
        {
            Summary.Add(new(d.IsPrimary ? "Ana ekran" : "Ekran",
                $"{d.MonitorName ?? d.DeviceName}\n{d.Current.Width}×{d.Current.Height} @ {d.Current.RefreshHz} Hz"));
        }

        foreach (var disk in inv.Disks)
        {
            var media = disk.MediaType switch
            {
                DiskMediaType.Ssd => "SSD",
                DiskMediaType.Hdd => "HDD",
                _ => "disk",
            };
            Summary.Add(new($"Disk {disk.RootPath.TrimEnd('\\')}",
                $"{media}{(disk.BusType is null ? "" : $" ({disk.BusType})")}\n{disk.FreeBytes / GiB:0} GB boş / {disk.TotalBytes / GiB:0} GB"));
        }

        if (inv.Power is { } power)
        {
            var source = power.HasBattery
                ? (power.OnAcPower ? "Dizüstü, şarjda" : $"Dizüstü, pilde (%{power.BatteryPercent})")
                : "Masaüstü";
            Summary.Add(new("Güç", $"{source}\n{power.ActiveSchemeName ?? "Bilinmeyen plan"}"));
        }
    }
}
