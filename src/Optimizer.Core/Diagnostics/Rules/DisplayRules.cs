using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

/// <summary>A monitor running below the refresh rate it supports — the single most common big loss.</summary>
public sealed class RefreshRateRule : IRule
{
    // 59.94 Hz modes are reported as 59, so allow one hertz of slack.
    private const int ToleranceHz = 1;
    private const int HighRefreshHz = 100;

    public string Id => "display.refresh-rate";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        foreach (var display in inventory.Displays)
        {
            var name = DisplayLabel(display);
            var current = display.Current.RefreshHz;

            if (display.MaxRefreshHzAtCurrentResolution - current > ToleranceHz)
            {
                yield return new Finding
                {
                    Id = $"{Id}:{display.DeviceName}",
                    Category = Category.Display,
                    Severity = Severity.Warning,
                    Impact = Impact.High,
                    Title = $"{name} {current} Hz'de çalışıyor, {display.MaxRefreshHzAtCurrentResolution} Hz destekliyor",
                    Detail = "Ekran desteklediği en yüksek yenileme hızında çalışmıyor. Oyun daha yüksek FPS üretse bile ekranda en fazla "
                             + $"{current} kare görürsünüz.",
                    Recommendation = "Ayarlar > Sistem > Ekran > Gelişmiş ekran > Yenileme hızı seçin bölümünden "
                                     + $"{display.MaxRefreshHzAtCurrentResolution} Hz'i seçin.",
                };
            }
            // Ordinary 60 Hz monitors list legacy 75 Hz modes (1024x768 etc.) in their EDID; only a
            // genuinely high-refresh panel limited at its current resolution points to a cable/port limit.
            else if (display.MaxRefreshHzAnyResolution >= HighRefreshHz
                     && display.MaxRefreshHzAnyResolution - display.MaxRefreshHzAtCurrentResolution > ToleranceHz)
            {
                yield return new Finding
                {
                    Id = $"{Id}:{display.DeviceName}",
                    Category = Category.Display,
                    Severity = Severity.Info,
                    Impact = Impact.Medium,
                    Title = $"{name} bu çözünürlükte en fazla {display.MaxRefreshHzAtCurrentResolution} Hz sunuyor",
                    Detail = $"Başka bir çözünürlükte {display.MaxRefreshHzAnyResolution} Hz'e kadar çıkabiliyor. "
                             + "Bu genellikle kablonun ya da bağlantı noktasının bant genişliği sınırından kaynaklanır.",
                    Recommendation = "DisplayPort ya da HDMI 2.1 destekli bir kablo ve ekran kartındaki doğru bağlantı noktasını kullandığınızdan emin olun.",
                };
            }
            else
            {
                yield return new Finding
                {
                    Id = $"{Id}:{display.DeviceName}",
                    Category = Category.Display,
                    Severity = Severity.Ok,
                    Title = $"{name} en yüksek yenileme hızında ({current} Hz)",
                };
            }
        }
    }

    internal static string DisplayLabel(DisplayInfo display) =>
        string.IsNullOrWhiteSpace(display.MonitorName)
            ? (display.IsPrimary ? "Ana ekran" : display.DeviceName)
            : display.MonitorName!;
}

/// <summary>
/// Desktop with a discrete GPU but a monitor plugged into the motherboard: the game
/// renders on the iGPU or pays a copy penalty. Laptops route through the iGPU by design.
/// </summary>
public sealed class MonitorOnIntegratedGpuRule : IRule
{
    public string Id => "display.connected-to-igpu";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.IsPortable)
            yield break;

        var discrete = inventory.Gpus.Where(g => g.Kind == GpuKind.Discrete).ToList();
        if (discrete.Count == 0)
            yield break;

        var integratedNames = inventory.Gpus
            .Where(g => g.Kind == GpuKind.Integrated)
            .Select(g => g.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var onIntegrated = inventory.Displays
            .Where(d => d.AdapterName is not null && integratedNames.Contains(d.AdapterName))
            .ToList();

        if (onIntegrated.Count == 0)
        {
            if (inventory.Displays.Count > 0)
            {
                yield return new Finding
                {
                    Id = Id,
                    Category = Category.Display,
                    Severity = Severity.Ok,
                    Title = $"Ekranlar harici ekran kartına ({discrete[0].Name}) bağlı",
                };
            }

            yield break;
        }

        foreach (var display in onIntegrated)
        {
            yield return new Finding
            {
                Id = $"{Id}:{display.DeviceName}",
                Category = Category.Display,
                Severity = Severity.Warning,
                Impact = Impact.High,
                Title = $"{RefreshRateRule.DisplayLabel(display)} anakarttaki görüntü çıkışına bağlı",
                Detail = $"Bu ekran işlemcinin dahili grafik birimine ({display.AdapterName}) bağlı. Sistemde {discrete[0].Name} varken "
                         + "oyunlar dahili grafikte çalışabilir ya da her kare iki kart arasında kopyalanır; ikisi de ciddi performans kaybıdır.",
                Recommendation = "Monitör kablosunu anakarttaki çıkıştan çıkarıp ekran kartının (kasanın arkasında, daha aşağıdaki yatay yuvalar) "
                                 + "çıkışına takın.",
            };
        }
    }
}
