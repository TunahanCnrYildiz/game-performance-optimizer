using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

/// <summary>Laptops with both an iGPU and a dGPU: games may start on the weak one.</summary>
public sealed class HybridGpuRule : IRule
{
    public string Id => "graphics.hybrid";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (!inventory.IsPortable)
            yield break;

        var integrated = inventory.Gpus.FirstOrDefault(g => g.Kind == GpuKind.Integrated);
        var discrete = inventory.Gpus.FirstOrDefault(g => g.Kind == GpuKind.Discrete);
        if (integrated is null || discrete is null)
            yield break;

        var highPerfCount = inventory.Gaming?.GpuPreferences.Count(p => p.Preference == 2) ?? 0;

        yield return new Finding
        {
            Id = Id,
            Category = Category.Graphics,
            Severity = Severity.Suggestion,
            Impact = Impact.High,
            Title = "Oyunların harici ekran kartında çalıştığından emin olun",
            Detail = $"Bu bilgisayarda hem {integrated.Name} hem {discrete.Name} var. Windows bazı oyunları güç tasarrufu için "
                     + $"dahili grafikte başlatabilir. Şu anda {highPerfCount} uygulama için 'Yüksek performans' tercihi tanımlı.",
            Recommendation = "Ayarlar > Sistem > Ekran > Grafik bölümünde oyununuzu seçip 'Yüksek performans'ı işaretleyin. "
                             + "Dizüstünüzde MUX anahtarı (ör. 'Yalnızca dGPU' / 'Ultimate' modu) varsa üreticinin uygulamasından açın.",
        };
    }
}

public sealed class GpuDriverAgeRule : IRule
{
    public string Id => "graphics.driver-age";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var relevant = inventory.Gpus.Where(g => g.Kind == GpuKind.Discrete).ToList();
        if (relevant.Count == 0)
            relevant = inventory.Gpus.Where(g => g.Kind == GpuKind.Integrated).ToList();

        var today = DateOnly.FromDateTime(inventory.CollectedAt.UtcDateTime);

        foreach (var gpu in relevant)
        {
            if (gpu.DriverDate is not { } date)
                continue;

            var ageDays = today.DayNumber - date.DayNumber;
            // An old driver is not a measured slowdown, only a likely missing fix for newer games.
            var (severity, impact) = ageDays switch
            {
                > 365 => (Severity.Suggestion, Impact.Low),
                > 180 => (Severity.Info, Impact.None),
                _ => (Severity.Ok, Impact.None),
            };

            yield return new Finding
            {
                Id = $"{Id}:{gpu.Name}",
                Category = Category.Graphics,
                Severity = severity,
                Impact = impact,
                Title = severity == Severity.Ok
                    ? $"{gpu.Name} sürücüsü güncel sayılır ({date:dd.MM.yyyy})"
                    : $"{gpu.Name} sürücüsü {ageDays / 30} aylık",
                Detail = severity == Severity.Ok
                    ? $"Sürücü sürümü {gpu.DriverVersion}."
                    : $"Sürücü sürümü {gpu.DriverVersion}, tarihi {date:dd.MM.yyyy}. Eski oyunlarda fark yaratmayabilir; ancak son çıkan "
                      + "oyunlara yönelik düzeltmeler ve iyileştirmeler yalnızca yeni sürücülerle gelir.",
                Recommendation = severity == Severity.Ok ? null : DriverSource(gpu.Vendor),
            };
        }
    }

    private static string DriverSource(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA App üzerinden ya da nvidia.com/drivers adresinden güncel sürücüyü kurun.",
        GpuVendor.Amd => "AMD Software: Adrenalin Edition üzerinden ya da amd.com/support adresinden güncel sürücüyü kurun.",
        GpuVendor.Intel => "Intel Graphics Software ya da intel.com/support üzerinden güncel sürücüyü kurun.",
        _ => "Ekran kartı üreticisinin sitesinden güncel sürücüyü kurun.",
    };
}

/// <summary>
/// Hardware-accelerated GPU scheduling. Tests show near-identical FPS on vs off; the one
/// hard dependency is NVIDIA DLSS Frame Generation. AMD AFMF does not need it.
/// </summary>
public sealed class HagsRule : IRule
{
    public string Id => "graphics.hags";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var state = inventory.Gaming?.Hags ?? Toggle.Unknown;
        if (state == Toggle.Unknown)
            yield break;

        if (state == Toggle.On)
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Graphics,
                Severity = Severity.Ok,
                Title = "Donanım hızlandırmalı GPU zamanlaması açık",
            };
            yield break;
        }

        var nvidia = inventory.Gpus.Any(g => g.Kind == GpuKind.Discrete && g.Vendor == GpuVendor.Nvidia);
        if (!nvidia)
            yield break;

        yield return new Finding
        {
            Id = Id,
            Category = Category.Graphics,
            Severity = Severity.Suggestion,
            Impact = Impact.Low,
            Title = "Donanım hızlandırmalı GPU zamanlaması kapalı",
            Detail = "NVIDIA DLSS Kare Oluşturma (Frame Generation) bu ayar açık değilken çalışmaz. Bunun dışında yapılan testlerde "
                     + "açık ya da kapalı olmasının FPS'e etkisi genelde ölçülemeyecek kadar küçük.",
            Recommendation = "DLSS Kare Oluşturma kullanmak istiyorsanız Ayarlar > Sistem > Ekran > Grafik > Varsayılan grafik ayarlarını "
                             + "değiştir bölümünden açın ve bilgisayarı yeniden başlatın.",
            Caveat = "Bazı sürücü/oyun birleşimlerinde takılma bildirilmiş. Açtıktan sonra sorun görürseniz aynı yerden kapatabilirsiniz.",
        };
    }
}
