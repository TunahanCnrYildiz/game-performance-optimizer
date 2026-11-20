using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

public sealed class DiskSpaceRule : IRule
{
    private const long GiB = 1024L * 1024 * 1024;

    public string Id => "storage.free-space";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        foreach (var disk in inventory.Disks.Where(d => d.IsSystemDrive || d.GameLibraries.Count > 0))
        {
            var freeGiB = (double)disk.FreeBytes / GiB;
            var low = disk.IsSystemDrive
                ? freeGiB < 20 || disk.FreeRatio < 0.10
                : freeGiB < 10;

            if (!low)
                continue;

            yield return new Finding
            {
                Id = $"{Id}:{disk.RootPath}",
                Category = Category.Storage,
                Severity = Severity.Warning,
                Impact = disk.IsSystemDrive ? Impact.Medium : Impact.Low,
                Title = $"{disk.RootPath} sürücüsünde {freeGiB:0.#} GB boş alan kaldı",
                Detail = disk.IsSystemDrive
                    ? "Sistem diskinde yer azaldığında sayfa dosyası, gölgelendirici önbelleği ve güncellemeler sorun çıkarır; takılmalar başlar."
                    : "Oyun güncellemeleri bu sürücüde yer bulamayabilir.",
                Recommendation = "Kullanmadığınız oyunları kaldırın ya da Ayarlar > Sistem > Depolama > Temizleme önerileri'ni kullanın.",
            };
        }
    }
}

public sealed class GamesOnHddRule : IRule
{
    public string Id => "storage.games-on-hdd";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        var withLibraries = inventory.Disks.Where(d => d.GameLibraries.Count > 0).ToList();
        if (withLibraries.Count == 0)
            yield break;

        var onHdd = withLibraries.Where(d => d.MediaType == DiskMediaType.Hdd).ToList();
        if (onHdd.Count == 0)
        {
            if (withLibraries.All(d => d.MediaType == DiskMediaType.Ssd))
            {
                yield return new Finding
                {
                    Id = Id,
                    Category = Category.Storage,
                    Severity = Severity.Ok,
                    Title = "Oyun kütüphaneleri SSD üzerinde",
                };
            }

            yield break;
        }

        foreach (var disk in onHdd)
        {
            yield return new Finding
            {
                Id = $"{Id}:{disk.RootPath}",
                Category = Category.Storage,
                Severity = Severity.Suggestion,
                Impact = Impact.High,
                Title = $"{disk.RootPath} sürücüsündeki oyun kütüphanesi mekanik diskte (HDD)",
                Detail = $"Kütüphane: {string.Join(", ", disk.GameLibraries)}. Güncel oyunlar sürekli veri akışı yapar; HDD'de uzun yükleme süreleri "
                         + "ve doku yüklenirken takılmalar olur. Bazı oyunlar SSD'yi zorunlu tutuyor.",
                Recommendation = "Sık oynadığınız oyunları SSD'ye taşıyın (Steam: Ayarlar > Depolama > oyunu seçip 'Taşı').",
            };
        }
    }
}
