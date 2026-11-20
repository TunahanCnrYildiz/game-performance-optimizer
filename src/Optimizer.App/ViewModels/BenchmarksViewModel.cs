using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimizer.App.Services;
using Optimizer.Core.Benchmarks;
using Optimizer.Core.Sessions;

namespace Optimizer.App.ViewModels;

public sealed record TargetProcess(int Pid, string Name, string Title)
{
    public string Display => string.IsNullOrWhiteSpace(Title) || Title == Name ? $"{Name} (PID {Pid})" : $"{Name} — {Title} (PID {Pid})";
}

public sealed partial class BenchmarksViewModel : ObservableObject
{
    private readonly IFrameCapture _capture;
    private readonly BenchmarkStore _store;
    private readonly SessionMonitor _monitor;
    private readonly IUserDialogs _dialogs;
    private CancellationTokenSource? _cts;

    public BenchmarksViewModel(IFrameCapture capture, BenchmarkStore store, SessionMonitor monitor, IUserDialogs dialogs)
    {
        _capture = capture;
        _store = store;
        _monitor = monitor;
        _dialogs = dialogs;
        LoadRuns();
        RefreshTargets();
    }

    public bool CanAttach => _capture.CanAttach;
    public bool CanImport => _capture.CanImport;
    public bool IsUnavailable => _capture.UnavailableReason is not null;
    public string? UnavailableReason => _capture.UnavailableReason;
    public string? ImportHint => _capture.ImportHint;

    public ObservableCollection<TargetProcess> Targets { get; } = [];
    public ObservableCollection<BenchmarkRunViewModel> Runs { get; } = [];
    public ObservableCollection<string> Games { get; } = [];
    public ObservableCollection<string> Labels { get; } = [];
    public IReadOnlyList<int> DurationOptions { get; } = [15, 30, 60, 90];

    public bool HasRuns => Runs.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureCommand))]
    public partial TargetProcess? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial string Label { get; set; } = "Mevcut ayarlar";

    [ObservableProperty]
    public partial int Seconds { get; set; } = 30;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MeasureCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsMeasuring { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    public partial string? CompareGame { get; set; }

    [ObservableProperty]
    public partial string? LabelA { get; set; }

    [ObservableProperty]
    public partial string? LabelB { get; set; }

    [ObservableProperty]
    public partial string? ComparisonText { get; set; }

    public bool IsIdle => !IsMeasuring;

    partial void OnCompareGameChanged(string? value)
    {
        Labels.Clear();
        foreach (var label in Runs.Where(r => r.Run.GameName == value).Select(r => r.Run.Label).Distinct().Order())
            Labels.Add(label);
        LabelA = Labels.ElementAtOrDefault(0);
        LabelB = Labels.ElementAtOrDefault(1);
    }

    partial void OnLabelAChanged(string? value) => UpdateComparison();

    partial void OnLabelBChanged(string? value) => UpdateComparison();

    [RelayCommand]
    private void RefreshTargets()
    {
        var keep = SelectedTarget?.Pid;
        Targets.Clear();

        // The game of the running session first: that is almost always what the user wants to measure.
        if (_monitor.Current is { } session && TryTarget(session.GamePid) is { } game)
            Targets.Add(game);

        var self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses().OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || Targets.Any(t => t.Pid == p.Id))
                        continue;
                    Targets.Add(new TargetProcess(p.Id, p.ProcessName, p.MainWindowTitle));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }

        SelectedTarget = Targets.FirstOrDefault(t => t.Pid == keep) ?? Targets.FirstOrDefault();
    }

    private bool CanMeasure() => !IsMeasuring && SelectedTarget is not null && _capture.CanAttach;

    [RelayCommand(CanExecute = nameof(CanMeasure))]
    private async Task MeasureAsync()
    {
        var target = SelectedTarget!;
        var label = string.IsNullOrWhiteSpace(Label) ? "ölçüm" : Label.Trim();
        const int delay = 5;

        IsMeasuring = true;
        _cts = new CancellationTokenSource();
        try
        {
            SetStatus($"{delay} saniye içinde oyuna geçin ve her seferinde aynı sahnede oynayın. Ardından {Seconds} saniye ölçülecek; bitince buraya dönün.", false);
            var result = await _capture.CaptureAsync(new CaptureRequest(target.Pid, Seconds, delay), _cts.Token);

            if (!result.Success && result.MayNeedElevation)
            {
                var retry = await _dialogs.ConfirmAsync(
                    "Kare yakalanamadı",
                    $"{result.Error}\n\nOyun yönetici olarak çalışıyorsa ölçüm aracı (PresentMon) da yönetici izniyle çalışmalı. "
                    + "Windows bir izin penceresi gösterecek; uygulamanın kendisi yönetici olmayacak. Tekrar denensin mi?",
                    "Yönetici izniyle dene",
                    "Vazgeç");
                if (retry)
                {
                    SetStatus($"{delay} saniye içinde oyuna geçin; {Seconds} saniye ölçülecek.", false);
                    result = await _capture.CaptureAsync(new CaptureRequest(target.Pid, Seconds, delay, Elevated: true), _cts.Token);
                }
            }

            if (!result.Success)
            {
                SetStatus(result.Error ?? "Ölçüm başarısız.", true);
                return;
            }

            var summary = FrameStatistics.Compute(FrameStatistics.MainSwapChain(result.Frames));
            if (summary is null)
            {
                SetStatus($"Yeterli kare yakalanamadı ({result.Frames.Count}). Oyunun ölçüm boyunca ekranda olduğundan emin olun.", true);
                return;
            }

            var run = new BenchmarkRun
            {
                Id = Guid.NewGuid(),
                GameName = target.Name,
                Label = label,
                CapturedAt = DateTimeOffset.Now,
                Summary = summary,
            };
            _store.Save(run);
            LoadRuns();
            CompareGame = run.GameName;
            SetStatus($"Ölçüm kaydedildi: ortalama {summary.AvgFps:0} FPS, 1% low {summary.Low1PercentFps:0} FPS.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Ölçüm sırasında hata: {ex.Message}", true);
        }
        finally
        {
            IsMeasuring = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Linux: read a frame-time log the game wrote itself (MangoHud) and save it like a measurement.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        var path = await _dialogs.PickFileAsync("Kare süresi kaydı seçin", _capture.ImportPatterns);
        if (path is null)
            return;

        try
        {
            var (game, frames) = _capture.Import(path);
            var summary = FrameStatistics.Compute(FrameStatistics.MainSwapChain(frames));
            if (summary is null)
            {
                SetStatus($"Kayıtta yeterli kare yok ({frames.Count}).", true);
                return;
            }

            var run = new BenchmarkRun
            {
                Id = Guid.NewGuid(),
                GameName = game ?? Path.GetFileNameWithoutExtension(path),
                Label = string.IsNullOrWhiteSpace(Label) ? "ölçüm" : Label.Trim(),
                CapturedAt = File.GetLastWriteTime(path),
                Summary = summary,
            };
            _store.Save(run);
            LoadRuns();
            CompareGame = run.GameName;
            SetStatus($"Kayıt içe aktarıldı: ortalama {summary.AvgFps:0} FPS, 1% low {summary.Low1PercentFps:0} FPS.", false);
        }
        catch (Exception ex)
        {
            SetStatus($"Kayıt okunamadı: {ex.Message}", true);
        }
    }

    private void Delete(BenchmarkRunViewModel run)
    {
        _store.Delete(run.Run.Id);
        LoadRuns();
    }

    private void LoadRuns()
    {
        var game = CompareGame;
        Runs.Clear();
        foreach (var run in _store.LoadAll())
            Runs.Add(new BenchmarkRunViewModel(run, Delete));
        OnPropertyChanged(nameof(HasRuns));

        Games.Clear();
        foreach (var g in Runs.Select(r => r.Run.GameName).Distinct().Order())
            Games.Add(g);

        CompareGame = Games.Contains(game ?? "") ? game : Games.FirstOrDefault();
        OnCompareGameChanged(CompareGame);
    }

    private void UpdateComparison()
    {
        if (CompareGame is null || LabelA is null || LabelB is null || LabelA == LabelB)
        {
            ComparisonText = Labels.Count < 2
                ? "Karşılaştırma için aynı oyunda iki farklı etiketle ölçüm yapın (ör. \"Discord açık\" ve \"Discord kapalı\")."
                : "Karşılaştırmak için iki farklı etiket seçin.";
            return;
        }

        var a = Runs.Where(r => r.Run.GameName == CompareGame && r.Run.Label == LabelA).Select(r => r.Run).ToList();
        var b = Runs.Where(r => r.Run.GameName == CompareGame && r.Run.Label == LabelB).Select(r => r.Run).ToList();
        var c = BenchmarkComparison.Compare(LabelA, a, LabelB, b);

        string Line(string metric, MetricDelta d) =>
            $"{metric}: {d.A:0.0} → {d.B:0.0} FPS ({d.PercentChange:+0.0;-0.0;0}%). " + d.Verdict switch
            {
                Verdict.BBetter => $"\"{LabelB}\" daha iyi; fark ölçüm oynaklığından (±%{d.NoisePercent:0.0}) büyük.",
                Verdict.BWorse => $"\"{LabelB}\" daha kötü; fark ölçüm oynaklığından (±%{d.NoisePercent:0.0}) büyük.",
                _ => $"Anlamlı fark yok; fark ölçüm oynaklığı (±%{d.NoisePercent:0.0}) içinde.",
            };

        ComparisonText = string.Join("\n",
            Line("Ortalama", c.AvgFps),
            Line("1% low", c.Low1PercentFps),
            c.NoiseAssumed
                ? $"Not: \"{LabelA}\" için {c.RunsA}, \"{LabelB}\" için {c.RunsB} ölçüm var. Oynaklık ölçülemediği için ±%{BenchmarkComparison.DefaultNoisePercent} varsayıldı; her durumu en az 2 kez ölçün."
                : $"\"{LabelA}\": {c.RunsA} ölçüm, \"{LabelB}\": {c.RunsB} ölçüm.");
    }

    private void SetStatus(string text, bool isError)
    {
        StatusIsError = isError;
        Status = text;
    }

    private static TargetProcess? TryTarget(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new TargetProcess(p.Id, p.ProcessName, p.MainWindowTitle);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

public sealed class BenchmarkRunViewModel
{
    public BenchmarkRunViewModel(BenchmarkRun run, Action<BenchmarkRunViewModel> delete)
    {
        Run = run;
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    public BenchmarkRun Run { get; }
    public IRelayCommand DeleteCommand { get; }

    public string Title => $"{Run.GameName} — {Run.Label}";
    public string DateText => Run.CapturedAt.ToString("dd.MM.yyyy HH:mm");
    public string AvgText => $"{Run.Summary.AvgFps:0.0}";
    public string Low1Text => $"{Run.Summary.Low1PercentFps:0.0}";
    public string Low01Text => $"{Run.Summary.Low01PercentFps:0.0}";
    public string StutterText => $"{Run.Summary.Stutters}";

    public string Details =>
        $"{Run.Summary.Frames} kare, {Run.Summary.DurationSeconds:0} sn · medyan {Run.Summary.MedianFrameMs:0.0} ms · en uzun {Run.Summary.MaxFrameMs:0} ms · "
        + $"ekrana gelmeyen %{Run.Summary.DroppedPercent:0.0} · sınırlayan: " + Run.Summary.Bottleneck switch
        {
            Bottleneck.Gpu => "ekran kartı",
            Bottleneck.Cpu => "işlemci (tahmini)",
            Bottleneck.FrameCap => "kare sınırı / V-Sync",
            _ => "belirsiz",
        };
}
