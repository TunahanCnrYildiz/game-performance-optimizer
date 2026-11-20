using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;

namespace Optimizer.App.ViewModels;

public sealed class FindingViewModel
{
    internal static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#D64545"));
    internal static readonly IBrush SuggestionBrush = new SolidColorBrush(Color.Parse("#C98A00"));
    internal static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#3B7DD8"));
    internal static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#2E9E5B"));

    public FindingViewModel(Finding finding, ITweak? tweak, IReadOnlyList<Change> changes, Func<FindingViewModel, Task> apply)
    {
        Model = finding;
        Tweak = changes.Count > 0 ? tweak : null;
        Changes = changes;
        ApplyCommand = new AsyncRelayCommand(() => apply(this));
    }

    public Finding Model { get; }
    public ITweak? Tweak { get; }
    public IReadOnlyList<Change> Changes { get; }
    public IAsyncRelayCommand ApplyCommand { get; }

    public bool CanFix => Tweak is not null;
    public string FixTitle => Tweak?.Title ?? "";
    public IReadOnlyList<string> ChangeLines => Changes.Select(c => c.Describe()).ToList();

    public string FixNote => Tweak?.RequiresConfirmation == true
        ? "Uygulandıktan sonra 15 saniye içinde onaylamazsanız otomatik olarak geri alınır."
        : "Mevcut değerler önce kaydedilir; Değişiklik geçmişi sekmesinden tek tıkla geri alabilirsiniz.";

    public string Title => Model.Title;
    public string? Detail => Model.Detail;
    public string? Recommendation => Model.Recommendation;
    public string? Caveat => Model.Caveat;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Model.Detail);
    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Model.Recommendation) && Model.Severity > Severity.Ok;
    public bool HasCaveat => !string.IsNullOrWhiteSpace(Model.Caveat);
    public bool IsExpandable => HasDetail || HasRecommendation || HasCaveat || CanFix;

    public string SeverityText => Model.Severity switch
    {
        Severity.Warning => "UYARI",
        Severity.Suggestion => "ÖNERİ",
        Severity.Info => "BİLGİ",
        _ => "TAMAM",
    };

    public IBrush SeverityBrush => Model.Severity switch
    {
        Severity.Warning => WarningBrush,
        Severity.Suggestion => SuggestionBrush,
        Severity.Info => InfoBrush,
        _ => OkBrush,
    };

    public string CategoryText => Model.Category switch
    {
        Category.Display => "Ekran",
        Category.Graphics => "Grafik",
        Category.Memory => "Bellek",
        Category.Power => "Güç",
        Category.Windows => "Windows",
        Category.Security => "Güvenlik",
        Category.Storage => "Depolama",
        Category.Background => "Arka plan",
        Category.System => "Sistem",
        _ => "",
    };

    public string ImpactText => Model.Impact switch
    {
        Impact.High => "Etki: yüksek",
        Impact.Medium => "Etki: orta",
        Impact.Low => "Etki: düşük",
        _ => "",
    };

    public bool HasImpact => Model.Impact > Impact.None && Model.Severity > Severity.Ok;
}
