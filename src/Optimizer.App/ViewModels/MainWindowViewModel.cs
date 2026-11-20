using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Inventory;

namespace Optimizer.App.ViewModels;

public sealed record SummaryRow(string Label, string Value);

public sealed partial class MainWindowViewModel(IInventoryCollector collector, DiagnosticEngine engine) : ObservableObject
{
    private const double GiB = 1024d * 1024 * 1024;

    private IReadOnlyList<FindingViewModel> _allFindings = [];

    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<SummaryRow> Summary { get; } = [];
    public ObservableCollection<string> Errors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial bool ShowPassed { get; set; } = true;

    [ObservableProperty] public partial int WarningCount { get; set; }
    [ObservableProperty] public partial int SuggestionCount { get; set; }
    [ObservableProperty] public partial int InfoCount { get; set; }
    [ObservableProperty] public partial int OkCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial DateTimeOffset? LastScan { get; set; }

    public bool HasErrors => Errors.Count > 0;

    public string StatusText => IsScanning
        ? "Sistem taranıyor…"
        : LastScan is { } t
            ? $"Son tarama: {t:HH:mm:ss}"
            : "Henüz tarama yapılmadı";

    partial void OnShowPassedChanged(bool value) => ApplyFilter();

    private bool CanScan() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        try
        {
            var inventory = await collector.CollectAsync();
            var findings = await Task.Run(() => engine.Evaluate(inventory));

            _allFindings = findings.Select(f => new FindingViewModel(f)).ToList();
            WarningCount = findings.Count(f => f.Severity == Severity.Warning);
            SuggestionCount = findings.Count(f => f.Severity == Severity.Suggestion);
            InfoCount = findings.Count(f => f.Severity == Severity.Info);
            OkCount = findings.Count(f => f.Severity == Severity.Ok);

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
        finally
        {
            IsScanning = false;
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
