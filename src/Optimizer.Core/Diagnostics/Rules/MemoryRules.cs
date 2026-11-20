using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

/// <summary>
/// Desktop RAM running at the JEDEC fallback speed usually means XMP/EXPO is off.
/// Laptop memory is soldered or locked by the vendor, so the rule skips portables.
/// </summary>
public sealed class MemorySpeedRule : IRule
{
    public string Id => "memory.speed";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.IsPortable || inventory.Memory is not { Modules.Count: > 0 } memory)
            yield break;

        var configured = memory.ConfiguredSpeedMts;
        if (configured <= 0)
            yield break;

        int? jedecFallback = memory.Generation switch
        {
            MemoryGeneration.Ddr4 => 2400,
            MemoryGeneration.Ddr5 => 4800,
            _ => null,
        };

        if (jedecFallback is null)
            yield break;

        // Windows reports the module's base (JEDEC) speed as "rated"; running above it means a profile is on.
        var rated = memory.Modules.Max(m => m.RatedSpeedMts);
        if (rated > 0 && configured > rated)
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Memory,
                Severity = Severity.Ok,
                Title = $"RAM {configured} MT/s'de çalışıyor (XMP/EXPO profili etkin)",
            };
            yield break;
        }

        if (configured <= jedecFallback)
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Memory,
                Severity = Severity.Suggestion,
                Impact = Impact.Medium,
                Title = $"RAM {configured} MT/s'de çalışıyor — XMP/EXPO kapalı olabilir",
                Detail = $"{GenerationName(memory.Generation)} bellekler BIOS'ta profil açılmadıkça standart hızda ({jedecFallback} MT/s) çalışır. "
                         + "Daha hızlı bir kit aldıysanız bu hız boşa gidiyor; özellikle işlemcinin darboğaz olduğu oyunlarda fark edilir.",
                Recommendation = "BIOS'a girip XMP (Intel) ya da EXPO (AMD) profilini etkinleştirin. Bu ayar yazılımdan güvenli şekilde yapılamaz.",
                Caveat = "Belleğin etiketindeki hız bu değerden yüksek değilse yapılacak bir şey yok. Profil açıldıktan sonra sistem kararsızlaşırsa BIOS'tan geri kapatın.",
            };
        }
        else
        {
            // Above the lowest JEDEC speed but not above the module's own base speed: either a fast
            // JEDEC kit (e.g. DDR5-5600) or a faster kit whose profile is off. We cannot tell which.
            yield return new Finding
            {
                Id = Id,
                Category = Category.Memory,
                Severity = Severity.Info,
                Title = $"RAM {configured} MT/s'de, standart (JEDEC) hızında çalışıyor",
                Detail = "Belleğin etiketindeki hız bu değerle aynıysa her şey yolunda. Etiket daha yüksek bir hız gösteriyorsa "
                         + "(ör. 6000 MT/s) XMP/EXPO profili açık değildir.",
                Recommendation = "Etiket daha hızlıysa BIOS'tan XMP (Intel) ya da EXPO (AMD) profilini etkinleştirin.",
            };
        }
    }

    internal static string GenerationName(MemoryGeneration generation) => generation switch
    {
        MemoryGeneration.Ddr3 => "DDR3",
        MemoryGeneration.Ddr4 => "DDR4",
        MemoryGeneration.Ddr5 => "DDR5",
        MemoryGeneration.Lpddr4 => "LPDDR4",
        MemoryGeneration.Lpddr5 => "LPDDR5",
        _ => "Bu",
    };
}

public sealed class MemoryChannelRule : IRule
{
    private const long GiB = 1024L * 1024 * 1024;

    public string Id => "memory.layout";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.Memory is not { } memory)
            yield break;

        var socketed = memory.Generation is MemoryGeneration.Ddr4 or MemoryGeneration.Ddr5 or MemoryGeneration.Ddr3;
        if (socketed && memory.Modules.Count == 1)
        {
            yield return new Finding
            {
                Id = $"{Id}.single-channel",
                Category = Category.Memory,
                Severity = Severity.Suggestion,
                Impact = Impact.Medium,
                Title = "Tek bellek modülü takılı (tek kanal)",
                Detail = "Tek modülle bellek bant genişliği çift kanala göre yaklaşık yarıya iner. Bu, özellikle dahili grafikli sistemlerde ve "
                         + "işlemci yoğun oyunlarda belirgin FPS kaybı demektir.",
                Recommendation = "Aynı modelden ikinci bir modül ekleyin ya da 2'li bir kitle değiştirin. Anakart kılavuzundaki önerilen yuvaları kullanın.",
            };
        }

        var totalGiB = (double)memory.TotalBytes / GiB;
        if (totalGiB < 7.5)
        {
            yield return new Finding
            {
                Id = $"{Id}.capacity",
                Category = Category.Memory,
                Severity = Severity.Warning,
                Impact = Impact.High,
                Title = $"Toplam bellek {totalGiB:0} GB",
                Detail = "Güncel oyunların çoğu en az 16 GB ister. 8 GB'ın altında takılmalar ve diske taşma kaçınılmazdır.",
                Recommendation = "En az 16 GB'a (2×8 GB) yükseltin.",
            };
        }
        else if (totalGiB < 15.5)
        {
            yield return new Finding
            {
                Id = $"{Id}.capacity",
                Category = Category.Memory,
                Severity = Severity.Suggestion,
                Impact = Impact.Medium,
                Title = $"Toplam bellek {totalGiB:0} GB",
                Detail = "Yeni oyunlar arka planda tarayıcı ve Discord açıkken 8 GB'ı kolayca aşar.",
                Recommendation = "Mümkünse 16 GB ya da üzerine çıkın; o zamana kadar oyun sırasında tarayıcıyı kapatmak yardımcı olur.",
            };
        }
        else if (memory.Modules.Count != 1 || !socketed)
        {
            yield return new Finding
            {
                Id = $"{Id}.capacity",
                Category = Category.Memory,
                Severity = Severity.Ok,
                Title = $"{totalGiB:0} GB bellek, {memory.Modules.Count} modül",
            };
        }
    }
}
