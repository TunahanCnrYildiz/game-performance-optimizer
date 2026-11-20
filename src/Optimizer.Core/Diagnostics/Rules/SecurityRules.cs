using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

/// <summary>
/// Memory Integrity is reported, never recommended off by default: it is a security
/// trade-off, and Windows may re-enable it through updates.
/// </summary>
public sealed class MemoryIntegrityRule : IRule
{
    public string Id => "security.memory-integrity";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var state = inventory.Security?.MemoryIntegrity ?? Toggle.Unknown;
        if (state == Toggle.Unknown)
            yield break;

        if (state == Toggle.Off)
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Security,
                Severity = Severity.Info,
                Title = "Bellek Bütünlüğü (Memory Integrity) kapalı",
                Detail = "Performans açısından en hızlı durum budur. Microsoft 2026 sonbaharından itibaren bu özelliği güncellemelerle "
                         + "uygun bilgisayarlarda otomatik açıyor; ileride FPS düşerse ilk bakılacak yerlerden biri burası.",
            };
            yield break;
        }

        yield return new Finding
        {
            Id = Id,
            Category = Category.Security,
            Severity = Severity.Info,
            Impact = Impact.Medium,
            Title = "Bellek Bütünlüğü (Memory Integrity) açık",
            Detail = "Bu güvenlik özelliği, işlemcinin darboğaz olduğu oyunlarda yapılan testlere göre yaklaşık %0–15 FPS kaybına yol açabiliyor; "
                     + "çoğu oyunda fark ölçülemeyecek kadar küçük.",
            Recommendation = "Yalnızca işlemci darboğazlı oyunlarda belirgin sorun yaşıyorsanız Windows Güvenliği > Cihaz güvenliği > "
                             + "Çekirdek yalıtımı bölümünden kapatmayı düşünün.",
            Caveat = "Kapatmak, kötü amaçlı veya zafiyetli sürücülere karşı önemli bir koruma katmanını kaldırır. Bazı anti-cheat sistemleri "
                     + "güvenlik durumunu giderek daha sıkı denetliyor.",
        };
    }
}

public sealed class SecureBootRule : IRule
{
    public string Id => "security.secure-boot";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var state = inventory.Security?.SecureBoot ?? Toggle.Unknown;
        if (state == Toggle.Unknown)
            yield break;

        yield return state == Toggle.On
            ? new Finding { Id = Id, Category = Category.Security, Severity = Severity.Ok, Title = "Güvenli Önyükleme (Secure Boot) açık" }
            : new Finding
            {
                Id = Id,
                Category = Category.Security,
                Severity = Severity.Suggestion,
                Impact = Impact.High,
                Title = "Güvenli Önyükleme (Secure Boot) kapalı",
                Detail = "Performansı etkilemez, ancak Battlefield 6, Valorant ve FACEIT gibi anti-cheat kullanan bazı oyunlar Secure Boot "
                         + "kapalıyken hiç açılmaz.",
                Recommendation = "BIOS/UEFI ayarlarından Secure Boot'u etkinleştirin.",
                Caveat = "Sistem eski (Legacy/CSM) modda kuruluysa önce diskin GPT'ye dönüştürülmesi gerekir; aksi hâlde Windows açılmaz. "
                         + "Emin değilseniz bu adımı bir bilene danışın.",
            };
    }
}
