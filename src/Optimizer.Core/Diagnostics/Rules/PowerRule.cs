using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

public sealed class PowerRule : IRule
{
    public string Id => "power";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.Power is not { } power)
            yield break;

        var problems = 0;

        if (power.BatterySaverOn)
        {
            problems++;
            yield return new Finding
            {
                Id = $"{Id}.battery-saver",
                Category = Category.Power,
                Severity = Severity.Warning,
                Impact = Impact.High,
                Title = "Pil tasarrufu açık",
                Detail = "Pil tasarrufu işlemci ve ekran kartı hızını belirgin şekilde düşürür ve arka plan işlerini kısıtlar.",
                Recommendation = "Oyun oynarken hızlı ayarlar menüsünden pil tasarrufunu kapatın.",
            };
        }

        if (power.HasBattery && !power.OnAcPower)
        {
            problems++;
            yield return new Finding
            {
                Id = $"{Id}.on-battery",
                Category = Category.Power,
                Severity = Severity.Suggestion,
                Impact = Impact.High,
                Title = "Bilgisayar pilde çalışıyor",
                Detail = "Dizüstü bilgisayarlar pildeyken ekran kartı ve işlemcinin güç sınırını düşürür; ne kadar düşürdüğü modele göre "
                         + "değişir ama oyunlarda genelde belirgin FPS kaybı olur.",
                Recommendation = "Oyun oynarken şarj aletini takın; mümkünse üreticinin orijinal, tam güçlü adaptörünü kullanın.",
            };
        }

        if (power.ActiveSchemeGuid == PowerInfo.PowerSaver)
        {
            problems++;
            yield return new Finding
            {
                Id = $"{Id}.scheme",
                Category = Category.Power,
                Severity = Severity.Warning,
                Impact = Impact.High,
                Title = $"Güç planı: {power.ActiveSchemeName ?? "Güç tasarrufu"}",
                Detail = "Güç tasarrufu planı işlemcinin hızlanmasını sınırlar.",
                Recommendation = "Denetim Masası > Güç Seçenekleri'nden 'Dengeli' ya da 'Yüksek performans' planını seçin.",
            };
        }

        var overlay = power.OnAcPower || !power.HasBattery ? power.OverlayOnAc : power.OverlayOnBattery;
        if (overlay == PowerModeOverlay.BestPowerEfficiency)
        {
            problems++;
            yield return new Finding
            {
                Id = $"{Id}.mode",
                Category = Category.Power,
                Severity = Severity.Suggestion,
                Impact = Impact.Medium,
                Title = "Güç modu 'En iyi güç verimliliği'nde",
                Detail = "Bu mod işlemcinin hızlanma davranışını kısar.",
                Recommendation = "Ayarlar > Sistem > Güç ve pil > Güç modu'nu 'Dengeli' ya da 'En iyi performans' yapın.",
            };
        }

        if (problems == 0)
        {
            yield return new Finding
            {
                Id = $"{Id}.ok",
                Category = Category.Power,
                Severity = Severity.Ok,
                Title = $"Güç ayarları performansı kısıtlamıyor ({power.ActiveSchemeName ?? "bilinmeyen plan"})",
                Detail = power.HasBattery
                    ? null
                    : "Masaüstünde ileride oyun açıkken geçici olarak 'Yüksek performans' planına geçmek, oyun kapanınca geri dönmek mümkün olacak.",
            };
        }
    }
}
