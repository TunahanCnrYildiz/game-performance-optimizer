using System.Text.RegularExpressions;
using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

/// <summary>
/// Game Mode is not a general recommendation: on current Windows 11 tests it is roughly
/// neutral and can hurt 1% lows in some CPU-bound games. The one case where it clearly
/// matters is a dual-CCD Ryzen X3D, where AMD's V-Cache driver parks the non-cache CCD
/// only while Game Mode is active.
/// </summary>
public sealed partial class GameModeRule : IRule
{
    public string Id => "windows.game-mode";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var state = inventory.Gaming?.GameMode ?? Toggle.Unknown;
        if (state == Toggle.Unknown)
            yield break;

        var dualCcdX3d = IsDualCcdX3d(inventory.Cpu?.Name);

        if (state == Toggle.On)
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Windows,
                Severity = Severity.Ok,
                Title = "Oyun Modu açık",
                Detail = dualCcdX3d
                    ? "Bu işlemcide AMD'nin 3D V-Cache sürücüsü, oyunları önbellekli çekirdeklere yönlendirmek için Oyun Modu'na ihtiyaç duyar; açık kalmalı."
                    : "Güncel testlerde Oyun Modu ortalama FPS'i belirgin şekilde değiştirmiyor. İşlemcinin darboğaz olduğu bir oyunda "
                      + "takılma yaşarsanız kapatıp karşılaştırmayı deneyebilirsiniz.",
            };
            yield break;
        }

        if (dualCcdX3d)
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Windows,
                Severity = Severity.Warning,
                Impact = Impact.High,
                Title = "Oyun Modu kapalı — bu işlemcide gerekli",
                Detail = $"{inventory.Cpu!.Name} iki çekirdek grubundan (CCD) oluşur ve yalnızca birinde 3D V-Cache vardır. AMD'nin sürücüsü, "
                         + "oyun açıkken önbelleksiz grubu yalnızca Oyun Modu etkinken devre dışı bırakır. Oyun Modu kapalıyken oyun "
                         + "iş parçacıkları iki gruba dağılabilir ve gruplar arası gecikme yüzünden FPS düşer.",
                Recommendation = "Ayarlar > Oyun > Oyun Modu'nu açın ve AMD yonga seti sürücüsünün güncel olduğundan emin olun.",
            };
            yield break;
        }

        yield return new Finding
        {
            Id = Id,
            Category = Category.Windows,
            Severity = Severity.Info,
            Title = "Oyun Modu kapalı",
            Detail = "Bu sistemde Oyun Modu'nun kapalı olması bir sorun değil. Windows 11'deki güncel testlerde ortalama FPS'e etkisi "
                     + "yaklaşık ±%5 içinde kalıyor; bazı işlemci yoğun oyunlarda kapalıyken 1% low değerleri daha iyi çıkıyor. "
                     + "Windows 10 döneminde (2020) Oyun Modu'nun takılmaya yol açtığı bilinen bir hata vardı ve Microsoft bunu düzeltti.",
            Recommendation = "Kapalı bırakabilirsiniz. Açık olması yalnızca oyun sırasında Windows Update'in sürücü kurmasını ve "
                             + "yeniden başlatma bildirimlerini ertelemesi açısından işe yarar.",
        };
    }

    // Ryzen 9 7900X3D/7950X3D, 9900X3D/9950X3D and later "x9x0X3D" parts. Single-CCD
    // chips (7800X3D, 9800X3D) have nothing to park.
    internal static bool IsDualCcdX3d(string? cpuName) =>
        cpuName is not null && DualCcdX3dRegex().IsMatch(cpuName);

    [GeneratedRegex(@"Ryzen\s+9\s+\d{4}X3D", RegexOptions.IgnoreCase)]
    private static partial Regex DualCcdX3dRegex();
}

public sealed class BackgroundRecordingRule : IRule
{
    public string Id => "windows.background-recording";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var state = inventory.Gaming?.BackgroundRecording ?? Toggle.Unknown;
        if (state == Toggle.Unknown)
            yield break;

        yield return state == Toggle.Off
            ? new Finding { Id = Id, Category = Category.Windows, Severity = Severity.Ok, Title = "Arka planda oyun kaydı kapalı" }
            : new Finding
            {
                Id = Id,
                Category = Category.Windows,
                Severity = Severity.Suggestion,
                Impact = Impact.Low,
                Title = "Arka planda oyun kaydı açık ('Olanları kaydet')",
                Detail = "Xbox Game Bar oyun boyunca sürekli video kaydı tutuyor; ekran kartının kodlayıcısını ve diski kullanır. "
                         + "Güçlü sistemlerde maliyeti genelde birkaç FPS'i geçmiyor, zayıf sistemlerde daha belirgin. Microsoft da "
                         + "performans sorunu yaşayanlara ilk olarak bu ayarı kapatmayı öneriyor.",
                Recommendation = "Ayarlar > Oyun > Yakalamalar > 'Olanları kaydet'i kapatın.",
                Caveat = "Son anları klip olarak kaydetmek için bu özelliği kullanıyorsanız açık bırakın.",
            };
    }
}

public sealed class WindowedOptimizationsRule : IRule
{
    public string Id => "windows.windowed-optimizations";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var state = inventory.Gaming?.WindowedGameOptimizations ?? Toggle.Unknown;
        if (state == Toggle.Unknown)
            yield break;

        yield return state == Toggle.On
            ? new Finding { Id = Id, Category = Category.Windows, Severity = Severity.Ok, Title = "Pencereli oyunlar için iyileştirmeler açık" }
            : new Finding
            {
                Id = Id,
                Category = Category.Windows,
                Severity = Severity.Suggestion,
                Impact = Impact.Low,
                Title = "Pencereli oyunlar için iyileştirmeler kapalı",
                Detail = "Bu ayar eski DirectX 10/11 oyunlarının pencereli ve kenarlıksız modda daha düşük gecikmeyle çalışmasını, "
                         + "Otomatik HDR ve değişken yenileme hızını kullanabilmesini sağlar.",
                Recommendation = "Ayarlar > Sistem > Ekran > Grafik > Varsayılan grafik ayarlarını değiştir > 'Pencereli oyunlar için iyileştirmeler'i açın.",
                Caveat = "Yalnızca DirectX 10/11 kullanan ve pencereli/kenarlıksız çalışan oyunları etkiler. Bazı oyunlarda ekran yırtılmasına "
                         + "yol açtığı bildirilmiş; o oyun için aynı ekrandan tek tek kapatılabilir.",
            };
    }
}

public sealed class PointerPrecisionRule : IRule
{
    public string Id => "windows.pointer-precision";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.Gaming?.EnhancePointerPrecision != Toggle.On)
            yield break;

        yield return new Finding
        {
            Id = Id,
            Category = Category.Windows,
            Severity = Severity.Info,
            Impact = Impact.Low,
            Title = "Fare ivmesi ('İşaretçi hassasiyetini artır') açık",
            Detail = "İvme açıkken imlecin gittiği mesafe fareyi ne kadar hızlı hareket ettirdiğinize bağlıdır. Ham girdi kullanmayan oyunlarda "
                     + "nişan almayı tutarsızlaştırır. FPS'e etkisi yoktur.",
            Recommendation = "Nişancı oyunları oynuyorsanız Ayarlar > Bluetooth ve cihazlar > Fare > Ek fare ayarları > İşaretçi Seçenekleri'nden kapatabilirsiniz.",
        };
    }
}

public sealed class StartupItemsRule : IRule
{
    private const int Threshold = 10;

    public string Id => "background.startup";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var enabled = inventory.StartupItems.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0)
            yield break;

        var names = string.Join(", ", enabled.Select(s => s.Name).Take(15));

        yield return enabled.Count >= Threshold
            ? new Finding
            {
                Id = Id,
                Category = Category.Background,
                Severity = Severity.Suggestion,
                Impact = Impact.Low,
                Title = $"Windows ile birlikte {enabled.Count} program başlıyor",
                Detail = $"Başlangıç programları: {names}. Bunlar açılışı yavaşlatır; bir kısmı arka planda bellek ve işlemci kullanır ya da "
                         + "oyun sırasında güncelleme indirir. Oyun FPS'ine etkisi genelde küçüktür.",
                Recommendation = "Görev Yöneticisi > Başlangıç uygulamaları sekmesinden ihtiyaç duymadıklarınızı devre dışı bırakın (silmeyin).",
            }
            : new Finding
            {
                Id = Id,
                Category = Category.Background,
                Severity = Severity.Ok,
                Title = $"Başlangıçta {enabled.Count} program çalışıyor",
                Detail = names,
            };
    }
}
