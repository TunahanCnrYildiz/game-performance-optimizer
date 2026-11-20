using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

public sealed class GameModeRule : IRule
{
    public string Id => "windows.game-mode";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var state = inventory.Gaming?.GameMode ?? Toggle.Unknown;
        if (state == Toggle.Unknown)
            yield break;

        yield return state == Toggle.On
            ? new Finding { Id = Id, Category = Category.Windows, Severity = Severity.Ok, Title = "Oyun Modu açık" }
            : new Finding
            {
                Id = Id,
                Category = Category.Windows,
                Severity = Severity.Suggestion,
                Impact = Impact.Low,
                Title = "Oyun Modu kapalı",
                Detail = "Oyun Modu, oyun açıkken Windows Update'in sürücü kurmasını ve yeniden başlatma bildirimlerini engeller, "
                         + "oyuna kaynak önceliği verir.",
                Recommendation = "Ayarlar > Oyun > Oyun Modu'nu açın.",
            };
    }
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
                Impact = Impact.Medium,
                Title = "Arka planda oyun kaydı açık ('Olanları kaydet')",
                Detail = "Xbox Game Bar oyun boyunca sürekli video kaydı tutuyor. Bu, ekran kartının kodlayıcısını ve diski sürekli kullanır.",
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
                Detail = $"Başlangıç programları: {names}. Bunların çoğu arka planda bellek ve işlemci kullanır, bazıları oyun sırasında güncelleme indirir.",
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
