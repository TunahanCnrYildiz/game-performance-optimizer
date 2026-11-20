using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics.Rules;

/// <summary>Kernel default 65530 is too low for some games; distributions raised it for exactly that reason.</summary>
public sealed class LinuxMaxMapCountRule : IRule
{
    public const long Recommended = 1_048_576;

    public string Id => "linux.max-map-count";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.Linux?.MaxMapCount is not { } value)
            yield break;

        yield return value >= Recommended
            ? new Finding { Id = Id, Category = Category.Memory, Severity = Severity.Ok, Title = $"vm.max_map_count yeterli ({value})" }
            : new Finding
            {
                Id = Id,
                Category = Category.Memory,
                Severity = Severity.Suggestion,
                Impact = Impact.Medium,
                Title = $"vm.max_map_count düşük ({value})",
                Detail = "Bazı yeni oyunlar çekirdeğin varsayılan sınırı olan 65530 bellek eşlemesini aşınca çöküyor. Fedora, Ubuntu ve Arch bu yüzden "
                         + "varsayılanı 1048576'ya yükseltti; SteamOS daha da yüksek bir değer kullanıyor. Oyun oynamayan programlara etkisi yoktur.",
                Recommendation = "Değeri 1048576 yapın (Gelişmiş sekmesinden ya da /etc/sysctl.d altında bir dosyayla).",
            };
    }
}

public sealed class LinuxGameModeRule : IRule
{
    public string Id => "linux.gamemode";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.Linux is not { } linux)
            yield break;

        if (linux.GameModeInstalled)
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Power,
                Severity = Severity.Ok,
                Title = "Feral GameMode kurulu",
                Detail = "Oyunda devreye girmesi için Steam'de oyunun başlatma seçeneklerine 'gamemoderun %command%' ekleyin "
                         + "(Lutris ve Heroic'te ayarlardan açılır). Oyun açıkken işlemciyi performans moduna alır, oyuna öncelik verir ve oyun kapanınca her şeyi geri alır.",
            };
            yield break;
        }

        var install = linux.PackageManager switch
        {
            "apt" => "sudo apt install gamemode",
            "dnf" => "sudo dnf install gamemode",
            "pacman" => "sudo pacman -S gamemode lib32-gamemode",
            "zypper" => "sudo zypper install gamemoded",
            _ => "dağıtımınızın paket yöneticisiyle 'gamemode' paketini kurun",
        };

        yield return new Finding
        {
            Id = Id,
            Category = Category.Power,
            Severity = Severity.Suggestion,
            Impact = Impact.Low,
            Title = "Feral GameMode kurulu değil",
            Detail = "GameMode, oyun açıkken işlemciyi performans moduna alır, oyuna öncelik verir ve oyun kapanınca ayarları geri alır. "
                     + "Etkisi sisteme ve oyuna göre değişir; işlemci güç tasarrufu modundaysa fark daha belirgindir.",
            Recommendation = $"Kurmak için: {install}. Ardından Steam'de oyunun başlatma seçeneklerine 'gamemoderun %command%' ekleyin.",
        };
    }
}

public sealed class LinuxPowerProfileRule : IRule
{
    public string Id => "linux.power-profile";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.Linux?.PowerProfile is not { } profile)
            yield break;

        yield return profile == "power-saver"
            ? new Finding
            {
                Id = Id,
                Category = Category.Power,
                Severity = Severity.Warning,
                Impact = Impact.High,
                Title = "Güç profili: güç tasarrufu",
                Detail = "Güç tasarrufu profili işlemci (ve bazı sistemlerde ekran kartı) güç sınırlarını düşürür.",
                Recommendation = "Masaüstünüzün güç menüsünden 'Dengeli' ya da 'Performans' profilini seçin.",
            }
            : new Finding
            {
                Id = Id,
                Category = Category.Power,
                Severity = Severity.Ok,
                Title = $"Güç profili: {profile}",
            };
    }
}

/// <summary>
/// "powersave" only means slow with the old acpi-cpufreq driver. With amd-pstate-epp or
/// intel_pstate in active mode it is the normal governor and the EPP hint decides.
/// </summary>
public sealed class LinuxCpuGovernorRule : IRule
{
    public string Id => "linux.cpu-governor";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.Linux is not { CpuGovernor: { } governor } linux)
            yield break;

        var legacyDriver = linux.CpuFreqDriver is "acpi-cpufreq" or "cpufreq-dt";
        if (governor == "powersave" && legacyDriver)
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Power,
                Severity = Severity.Suggestion,
                Impact = Impact.Medium,
                Title = "İşlemci 'powersave' düzenleyicisinde (acpi-cpufreq)",
                Detail = "Bu sürücüyle 'powersave' işlemciyi en düşük frekansta tutar.",
                Recommendation = "'schedutil' ya da oyun sırasında 'performance' kullanın; Feral GameMode bunu oyun süresince otomatik yapar.",
            };
        }
        else if (linux.EnergyPerformancePreference == "power")
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Power,
                Severity = Severity.Suggestion,
                Impact = Impact.Medium,
                Title = "İşlemci enerji tercihi 'power' (en fazla tasarruf)",
                Detail = $"{linux.CpuFreqDriver} sürücüsünde işlemcinin ne kadar hızlanacağını bu tercih belirler.",
                Recommendation = "Güç profilini 'Dengeli' ya da 'Performans' yapın; profil bu tercihi de değiştirir.",
            };
        }
        else
        {
            yield return new Finding
            {
                Id = Id,
                Category = Category.Power,
                Severity = Severity.Ok,
                Title = $"İşlemci frekans yönetimi: {linux.CpuFreqDriver ?? "?"} / {governor}"
                        + (linux.EnergyPerformancePreference is { } epp ? $" / {epp}" : ""),
            };
        }
    }
}

/// <summary>
/// Since kernel 6.2 split-lock accesses are deliberately slowed down. A few games do them
/// and stutter as a result; SteamOS ships with the mitigation turned off.
/// </summary>
public sealed class LinuxSplitLockRule : IRule
{
    public string Id => "linux.split-lock";

    public IEnumerable<Finding> Evaluate(SystemInventory inventory)
    {
        if (inventory.Linux?.SplitLockMitigate != 1)
            yield break;

        yield return new Finding
        {
            Id = Id,
            Category = Category.System,
            Severity = Severity.Info,
            Impact = Impact.Low,
            Title = "Split-lock yavaşlatması açık (kernel.split_lock_mitigate = 1)",
            Detail = "Çekirdek 6.2'den beri, belleğe hizasız kilitli erişim yapan programlar kasıtlı olarak yavaşlatılıyor. Bunu yapan birkaç oyun "
                     + "takılma yaşıyor; SteamOS bu yüzden ayarı kapalı getiriyor. Çoğu oyun etkilenmez.",
            Recommendation = "Yalnızca bir oyunda açıklanamayan takılma görüyorsanız Gelişmiş sekmesinden kapatıp FPS ölçümüyle karşılaştırın.",
            Caveat = "Bu yavaşlatma, kötü niyetli bir programın sistemi kilitli erişimlerle yavaşlatmasına karşı bir korumadır.",
        };
    }
}
