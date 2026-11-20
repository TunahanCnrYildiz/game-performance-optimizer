using Avalonia.Media;
using Optimizer.Core.Diagnostics;

namespace Optimizer.App.ViewModels;

public sealed class FindingViewModel(Finding finding)
{
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#D64545"));
    private static readonly IBrush SuggestionBrush = new SolidColorBrush(Color.Parse("#C98A00"));
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#3B7DD8"));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.Parse("#2E9E5B"));

    public Finding Model { get; } = finding;

    public string Title => Model.Title;
    public string? Detail => Model.Detail;
    public string? Recommendation => Model.Recommendation;
    public string? Caveat => Model.Caveat;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Model.Detail);
    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Model.Recommendation) && Model.Severity > Severity.Ok;
    public bool HasCaveat => !string.IsNullOrWhiteSpace(Model.Caveat);
    public bool IsExpandable => HasDetail || HasRecommendation || HasCaveat;

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
