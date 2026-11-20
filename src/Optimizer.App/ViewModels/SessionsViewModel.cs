using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.Core.Sessions;

namespace Optimizer.App.ViewModels;

public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly SessionMonitor _monitor;
    private readonly SettingsStore _settings;
    private readonly SessionStore _store;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(15) };

    public SessionsViewModel(SessionMonitor monitor, SettingsStore settings, SessionStore store, bool supportsEfficiencyMode = true)
    {
        SupportsEfficiencyMode = supportsEfficiencyMode;
        _monitor = monitor;
        _settings = settings;
        _store = store;

        _monitor.SessionStarted += _ => Dispatcher.UIThread.Post(RefreshStatus);
        _monitor.SessionEnded += report => Dispatcher.UIThread.Post(() =>
        {
            RefreshStatus();
            Reports.Insert(0, CreateReport(report));
            OnPropertyChanged(nameof(HasReports));
            LastError = null;
        });
        _monitor.Error += message => Dispatcher.UIThread.Post(() => LastError = message);
        _clock.Tick += (_, _) => RefreshStatus();
        _clock.Start();

        IsMonitoring = _settings.Load().MonitorSessions;
        LoadEfficiencyList();
        foreach (var report in _store.LoadAll())
            Reports.Add(CreateReport(report));
        RefreshStatus();
    }

    public ObservableCollection<SessionReportViewModel> Reports { get; } = [];
    public ObservableCollection<EfficiencyAppViewModel> EfficiencyApps { get; } = [];

    public bool HasReports => Reports.Count > 0;

    /// <summary>
    /// False on Linux: an unprivileged process can lower another process's priority but not raise it
    /// back, so the change could not be undone after the game.
    /// </summary>
    public bool SupportsEfficiencyMode { get; }
    public bool HasEfficiencyApps => EfficiencyApps.Count > 0;

    [ObservableProperty]
    public partial bool IsMonitoring { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsInSession { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? LastError { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddEfficiencyAppCommand))]
    public partial string NewAppName { get; set; } = "";

    [ObservableProperty]
    public partial string? AddError { get; set; }

    public bool HasError => !string.IsNullOrEmpty(LastError);

    private bool _activated;

    /// <summary>
    /// Starts watching. Called only after startup recovery has reverted any session left
    /// Pending by a crash, so recovery never undoes a session that is just starting.
    /// </summary>
    public void Activate()
    {
        _activated = true;
        if (IsMonitoring)
            _monitor.Start();
        RefreshStatus();
    }

    partial void OnIsMonitoringChanged(bool value)
    {
        _settings.Update(s => s with { MonitorSessions = value });
        if (!_activated)
            return;

        if (value)
            _monitor.Start();
        else
            _monitor.Stop();
        RefreshStatus();
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewAppName);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddEfficiencyApp()
    {
        AddError = TryAddEfficiencyApp(NewAppName);
        if (AddError is null)
            NewAppName = "";
    }

    [RelayCommand]
    private void DismissError() => LastError = null;

    /// <summary>Returns an error message, or null on success.</summary>
    internal string? TryAddEfficiencyApp(string raw)
    {
        var name = SessionMonitor.NormalizeName(raw);
        if (name.Length == 0)
            return "Bir uygulama adı yazın (ör. Discord ya da chrome).";
        if (SessionMonitor.IsProtected(name))
            return $"{name} bir Windows bileşeni ya da anti-cheat; güvenlik için kısılamaz.";

        _settings.Update(s => s.EfficiencyModeApps.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? s
            : s with { EfficiencyModeApps = [.. s.EfficiencyModeApps, name] });
        LoadEfficiencyList();
        RefreshReportFlags();
        return null;
    }

    internal void RemoveEfficiencyApp(string name)
    {
        _settings.Update(s => s with
        {
            EfficiencyModeApps = s.EfficiencyModeApps.Where(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList(),
        });
        LoadEfficiencyList();
        RefreshReportFlags();
    }

    internal bool IsInEfficiencyList(string name) =>
        _settings.Load().EfficiencyModeApps.Contains(SessionMonitor.NormalizeName(name), StringComparer.OrdinalIgnoreCase);

    private void LoadEfficiencyList()
    {
        EfficiencyApps.Clear();
        foreach (var name in _settings.Load().EfficiencyModeApps.Order(StringComparer.OrdinalIgnoreCase))
            EfficiencyApps.Add(new EfficiencyAppViewModel(name, RemoveEfficiencyApp));
        OnPropertyChanged(nameof(HasEfficiencyApps));
    }

    private void RefreshReportFlags()
    {
        foreach (var report in Reports)
        foreach (var p in report.Processes)
            p.Refresh();
    }

    private void RefreshStatus()
    {
        var current = _monitor.Current;
        IsInSession = current is not null;
        StatusText = current is not null
            ? $"Şu an {current.GameName} oynanıyor ({(int)(DateTimeOffset.Now - current.StartedAt).TotalMinutes} dk). Ölçüm sürüyor."
            : IsMonitoring
                ? "İzleme açık: bir oyun başladığında ölçüm kendiliğinden başlar."
                : "İzleme kapalı.";
    }

    private SessionReportViewModel CreateReport(SessionReport report) => new(report, this);
}

public sealed class EfficiencyAppViewModel(string name, Action<string> remove)
{
    public string Name { get; } = name;
    public IRelayCommand RemoveCommand { get; } = new RelayCommand(() => remove(name));
}

public sealed class SessionReportViewModel
{
    private const double MiB = 1024 * 1024;

    public SessionReportViewModel(SessionReport report, SessionsViewModel owner)
    {
        Report = report;
        Processes = report.Background.Take(12).Select(p => new ProcessUsageViewModel(p, owner)).ToList();
    }

    public SessionReport Report { get; }
    public IReadOnlyList<ProcessUsageViewModel> Processes { get; }

    public string Title => Report.GameName;
    public string DateText => Report.StartedAt.ToString("dd.MM.yyyy HH:mm");
    public string DurationText => Report.Duration.TotalMinutes >= 1 ? $"{(int)Report.Duration.TotalMinutes} dk" : $"{(int)Report.Duration.TotalSeconds} sn";

    public string Summary
    {
        get
        {
            var cpu = Report.Background.Sum(p => p.AvgCpuPercent);
            var gpu = Math.Min(100, Report.Background.Sum(p => p.AvgGpuPercent));
            var io = Report.Background.Sum(p => p.AvgIoBytesPerSec) / MiB;
            var game = Report.Game is { } g ? $"Oyun ortalama %{g.AvgCpuPercent:0} işlemci, %{g.AvgGpuPercent:0} ekran kartı kullandı. " : "";
            return game + $"Oyun dışındaki süreçler toplam ortalama %{cpu:0.0} işlemci, %{gpu:0.0} ekran kartı ve {io:0.0} MB/s G/Ç kullandı.";
        }
    }

    public string CpuText => Report.Cpu is { } c
        ? $"İşlemci: ortalama %{c.AvgUtilizationPercent:0} kullanım"
          + (c.AvgEffectiveMhz > 0 ? $", ortalama {c.AvgEffectiveMhz:0} MHz (en düşük {c.MinEffectiveMhz:0} MHz)." : "; saat hızı bu sistemde okunamıyor.")
        : "İşlemci saat bilgisi okunamadı.";

    public bool IsCpuLimited => Report.Cpu is { LimitedSamplePercent: > 10 };

    public string LimitText => Report.Cpu is { } c
        ? $"İşlemci oturumun %{c.LimitedSamplePercent:0}'inde güç ya da ısı sınırına takıldı (izin verilen en düşük performans %{c.MinAllowedPerformancePercent:0}). "
          + "Soğutmayı, toz birikimini ve dizüstüyse güç modunu kontrol edin."
        : "";

    public bool HasEfficiency => Report.EfficiencyApplied.Count > 0;
    public string EfficiencyText => $"Verimlilik moduna alınanlar: {string.Join(", ", Report.EfficiencyApplied)}";
}

public sealed partial class ProcessUsageViewModel(ProcessUsage usage, SessionsViewModel owner) : ObservableObject
{
    private const double MiB = 1024 * 1024;

    public string Name => usage.Name;
    public string CpuText => $"{usage.AvgCpuPercent:0.0}";
    public string GpuText => $"{usage.AvgGpuPercent:0.0}";
    public string IoText => $"{usage.AvgIoBytesPerSec / MiB:0.00}";
    public string MemoryText => $"{usage.PeakWorkingSetBytes / MiB:0}";

    public bool CanThrottle => owner.SupportsEfficiencyMode && !SessionMonitor.IsProtected(usage.Name);
    public bool IsListed => owner.IsInEfficiencyList(usage.Name);
    public string ToggleText => IsListed ? "Listeden çıkar" : "Oyun sırasında kıs";

    [RelayCommand]
    private void Toggle()
    {
        if (IsListed)
            owner.RemoveEfficiencyApp(SessionMonitor.NormalizeName(usage.Name));
        else
            owner.TryAddEfficiencyApp(usage.Name);
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsListed));
        OnPropertyChanged(nameof(ToggleText));
    }
}
