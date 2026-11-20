using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using Optimizer.Core.Inventory;

namespace Optimizer.Platform.Linux;

/// <summary>Read-only scan of a Linux system through /proc, /sys and a few standard tools. No root needed.</summary>
public sealed class LinuxInventoryCollector(LinuxFs? fs = null, Func<string, string?>? env = null, Func<string, string, string?>? shell = null) : IInventoryCollector
{
    private readonly LinuxFs _fs = fs ?? LinuxFs.System;
    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;
    private readonly Func<string, string, string?> _shell = shell ?? ((f, a) => Shell.Run(f, a));

    public Task<SystemInventory> CollectAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var errors = new ConcurrentBag<CollectorError>();

        T? Try<T>(string name, Func<T> collect) where T : class
        {
            try
            {
                return collect();
            }
            catch (Exception ex)
            {
                errors.Add(new CollectorError(name, ex.Message));
                return null;
            }
        }

        var linux = Try("Linux", CollectLinux);
        return new SystemInventory
        {
            CollectedAt = DateTimeOffset.Now,
            Os = Try("İşletim sistemi", CollectOs),
            Cpu = Try("İşlemci", CollectCpu),
            Gpus = Try("Ekran kartı", CollectGpus) ?? [],
            Memory = Try("Bellek", CollectMemory),
            Displays = Try("Ekranlar", CollectDisplays) ?? [],
            Power = Try("Güç", () => CollectPower(linux?.PowerProfile)),
            Disks = Try("Diskler", CollectDisks) ?? [],
            Linux = linux,
            Errors = errors.ToList(),
        };
    }, cancellationToken);

    internal OsInfo CollectOs()
    {
        var release = ParseOsRelease(_fs.Read("/etc/os-release"));
        var kernel = _fs.ReadLine("/proc/sys/kernel/osrelease") ?? "?";
        return new OsInfo(
            release.GetValueOrDefault("PRETTY_NAME") ?? "Linux",
            kernel,
            0,
            RuntimeInformation.OSArchitecture.ToString());
    }

    internal CpuInfo CollectCpu()
    {
        var text = _fs.Read("/proc/cpuinfo") ?? throw new InvalidOperationException("/proc/cpuinfo okunamadı.");
        var blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        string? model = null;
        var cores = new HashSet<(string, string)>();
        var logical = 0;

        foreach (var block in blocks)
        {
            var fields = block.Split('\n')
                .Select(l => l.Split(':', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim());
            if (!fields.ContainsKey("processor"))
                continue;

            logical++;
            model ??= fields.GetValueOrDefault("model name") ?? fields.GetValueOrDefault("Model") ?? fields.GetValueOrDefault("Hardware");
            cores.Add((fields.GetValueOrDefault("physical id") ?? "0", fields.GetValueOrDefault("core id") ?? logical.ToString(CultureInfo.InvariantCulture)));
        }

        var maxKhz = _fs.ReadLong("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq") ?? 0;
        return new CpuInfo(model ?? "Bilinmeyen işlemci", cores.Count, logical, (int)(maxKhz / 1000));
    }

    internal MemoryInfo CollectMemory()
    {
        var line = (_fs.Read("/proc/meminfo") ?? "").Split('\n').FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
        var kb = line is null ? 0 : long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        // Module details need root (dmidecode); only the total is reported.
        return new MemoryInfo(kb * 1024, []);
    }

    internal IReadOnlyList<GpuInfo> CollectGpus()
    {
        var result = new List<GpuInfo>();
        foreach (var card in _fs.Directories("/sys/class/drm", "card*").Where(c => !Path.GetFileName(c).Contains('-')))
        {
            var device = $"{card}/device";
            var vendorId = _fs.ReadLine($"{device}/vendor")?.Replace("0x", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
            if (vendorId is null)
                continue;

            var driver = _fs.LinkTarget($"{device}/driver") is { } d ? Path.GetFileName(d.TrimEnd('/')) : null;
            var vendor = vendorId switch
            {
                "10DE" => GpuVendor.Nvidia,
                "1002" => GpuVendor.Amd,
                "8086" => GpuVendor.Intel,
                _ => GpuVendor.Unknown,
            };
            var vram = _fs.ReadLong($"{device}/mem_info_vram_total");
            var slot = _fs.LinkTarget(device) is { } target ? Path.GetFileName(target.TrimEnd('/')) : null;
            var name = slot is not null ? LspciName(slot) : null;

            const long oneGiB = 1024L * 1024 * 1024;
            var kind = (vendor, driver) switch
            {
                (GpuVendor.Nvidia, _) => GpuKind.Discrete,
                (GpuVendor.Intel, "i915") => GpuKind.Integrated,
                (GpuVendor.Amd, _) when vram is > 0 and < oneGiB => GpuKind.Integrated,
                (GpuVendor.Amd, _) when vram >= oneGiB => GpuKind.Discrete,
                _ => GpuKind.Unknown,
            };

            result.Add(new GpuInfo
            {
                Name = name ?? $"{vendor} ekran kartı ({driver ?? "sürücü yok"})",
                Vendor = vendor,
                Kind = kind,
                DriverVersion = DriverVersion(driver),
                DedicatedMemoryBytes = vram,
                PnpDeviceId = slot,
            });
        }

        return result;
    }

    private string? LspciName(string slot)
    {
        // "03:00.0 VGA compatible controller: Advanced Micro Devices, Inc. [AMD/ATI] Navi 48 [Radeon RX 9070 XT]"
        var line = _shell("lspci", $"-s {slot}")?.Split('\n')[0];
        var colon = line?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
        return colon > 0 ? line![(colon + 2)..].Trim() : null;
    }

    private string? DriverVersion(string? driver)
    {
        if (driver == "nvidia" && _fs.ReadLine("/proc/driver/nvidia/version") is { } nv)
        {
            var marker = nv.IndexOf("Kernel Module", StringComparison.Ordinal);
            return marker >= 0 ? "nvidia " + nv[(marker + 13)..].Trim().Split(' ', 2)[0] : nv;
        }

        return driver;
    }

    internal IReadOnlyList<DisplayInfo> CollectDisplays()
    {
        // Wayland compositors do not expose modes in a common way; xrandr (also via Xwayland) is the best common denominator.
        if (_env("DISPLAY") is null || _shell("xrandr", "--query") is not { } output)
            return [];
        return XrandrParser.Parse(output);
    }

    internal PowerInfo CollectPower(string? profile)
    {
        var hasBattery = false;
        var onAc = true;
        int? percent = null;
        var sawMains = false;

        foreach (var supply in _fs.Directories("/sys/class/power_supply"))
        {
            switch (_fs.ReadLine($"{supply}/type"))
            {
                case "Battery" when _fs.ReadLine($"{supply}/scope") != "Device":
                    hasBattery = true;
                    percent = (int?)_fs.ReadLong($"{supply}/capacity");
                    break;
                case "Mains":
                    sawMains = true;
                    onAc = _fs.ReadLine($"{supply}/online") == "1";
                    break;
            }
        }

        return new PowerInfo
        {
            HasBattery = hasBattery,
            OnAcPower = !hasBattery || !sawMains || onAc,
            BatteryPercent = percent,
            BatterySaverOn = false,
            ActiveSchemeName = profile,
        };
    }

    internal IReadOnlyList<DiskInfo> CollectDisks()
    {
        var mounts = (_fs.Read("/proc/mounts") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(' '))
            .Where(p => p.Length >= 3 && p[0].StartsWith("/dev/", StringComparison.Ordinal) && !p[0].StartsWith("/dev/loop", StringComparison.Ordinal))
            .Select(p => (Device: p[0], Mount: p[1].Replace("\\040", " ")))
            .Where(m => !m.Mount.StartsWith("/boot", StringComparison.Ordinal) && !m.Mount.StartsWith("/snap", StringComparison.Ordinal))
            // Containers bind single files (/etc/hosts, /etc/resolv.conf) from the host disk; only directories are real mounts.
            .Where(m => Directory.Exists(_fs.Path(m.Mount)))
            .DistinctBy(m => m.Device)
            .ToList();

        var libraries = SteamLibraries.FindGameRoots(_fs, _env("HOME"));
        var result = new List<DiskInfo>();
        foreach (var (device, mount) in mounts)
        {
            long total = 0, free = 0;
            if (_fs.Root == "/")
            {
                try
                {
                    var drive = new DriveInfo(mount);
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    continue;
                }
            }

            var mountPrefix = mount.TrimEnd('/') + "/";
            result.Add(new DiskInfo
            {
                RootPath = mount,
                Label = device,
                TotalBytes = total,
                FreeBytes = free,
                IsSystemDrive = mount == "/",
                MediaType = MediaType(device),
                BusType = Path.GetFileName(device).StartsWith("nvme", StringComparison.Ordinal) ? "NVMe" : null,
                // Each library belongs to the most specific mount point containing it.
                GameLibraries = libraries
                    .Where(l => (l + "/").StartsWith(mountPrefix, StringComparison.Ordinal)
                                && !mounts.Any(o => o.Mount.Length > mount.Length && (l + "/").StartsWith(o.Mount.TrimEnd('/') + "/", StringComparison.Ordinal)))
                    .ToList(),
            });
        }

        return result;
    }

    private DiskMediaType MediaType(string device)
    {
        var name = Path.GetFileName(device);
        // Partition → parent block device: nvme0n1p2 → nvme0n1, mmcblk0p1 → mmcblk0, sda2 → sda.
        var match = global::System.Text.RegularExpressions.Regex.Match(name, @"^(nvme\d+n\d+|mmcblk\d+)(p\d+)?$");
        var parent = match.Success ? match.Groups[1].Value : name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return _fs.ReadLine($"/sys/block/{parent}/queue/rotational") switch
        {
            "1" => DiskMediaType.Hdd,
            "0" => DiskMediaType.Ssd,
            _ => DiskMediaType.Unknown,
        };
    }

    internal LinuxInfo CollectLinux()
    {
        var release = ParseOsRelease(_fs.Read("/etc/os-release"));
        const string cpufreq = "/sys/devices/system/cpu/cpu0/cpufreq";
        var thp = _fs.ReadLine("/sys/kernel/mm/transparent_hugepage/enabled");

        return new LinuxInfo
        {
            KernelVersion = _fs.ReadLine("/proc/sys/kernel/osrelease") ?? "?",
            Distribution = release.GetValueOrDefault("PRETTY_NAME"),
            SessionType = _env("XDG_SESSION_TYPE"),
            Desktop = _env("XDG_CURRENT_DESKTOP"),
            CpuGovernor = _fs.ReadLine($"{cpufreq}/scaling_governor"),
            CpuFreqDriver = _fs.ReadLine($"{cpufreq}/scaling_driver"),
            EnergyPerformancePreference = _fs.ReadLine($"{cpufreq}/energy_performance_preference"),
            PowerProfile = _shell("powerprofilesctl", "get")?.Trim() is { Length: > 0 } p
                ? p
                : _fs.ReadLine("/sys/firmware/acpi/platform_profile") switch
                {
                    "low-power" or "quiet" => "power-saver",
                    "balanced" => "balanced",
                    "performance" => "performance",
                    _ => null,
                },
            MaxMapCount = _fs.ReadLong("/proc/sys/vm/max_map_count"),
            SplitLockMitigate = (int?)_fs.ReadLong("/proc/sys/kernel/split_lock_mitigate"),
            GameModeInstalled = _fs.Exists("/usr/bin/gamemoded") || _fs.Exists("/usr/local/bin/gamemoded") || _fs.Exists("/usr/bin/gamemoderun"),
            GameModeRunning = _fs.Directories("/proc").Any(d => _fs.ReadLine($"{d}/comm") == "gamemoded"),
            SchedExtState = _fs.ReadLine("/sys/kernel/sched_ext/state"),
            SchedExtOps = _fs.ReadLine("/sys/kernel/sched_ext/root/ops") is { Length: > 0 } ops ? ops : null,
            NtsyncAvailable = _fs.Exists("/dev/ntsync"),
            TransparentHugePages = thp is null ? null : thp[(thp.IndexOf('[') + 1)..thp.IndexOf(']')],
            PackageManager = new[] { "apt", "dnf", "pacman", "zypper" }.FirstOrDefault(pm => _fs.Exists($"/usr/bin/{pm}")),
        };
    }

    internal static Dictionary<string, string> ParseOsRelease(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim().Trim('"'));
}

public static class XrandrParser
{
    /// <summary>Parses `xrandr --query`: output lines followed by indented mode lines; "*" marks the current rate.</summary>
    public static IReadOnlyList<DisplayInfo> Parse(string output)
    {
        var result = new List<DisplayInfo>();
        string? name = null;
        var primary = false;
        DisplayMode? current = null;
        var ratesByMode = new List<(int W, int H, int MaxHz)>();

        void Flush()
        {
            if (name is not null && current is not null)
            {
                result.Add(new DisplayInfo
                {
                    DeviceName = name,
                    MonitorName = name,
                    IsPrimary = primary,
                    Current = current,
                    MaxRefreshHzAtCurrentResolution = ratesByMode.Where(m => m.W == current.Width && m.H == current.Height).Select(m => m.MaxHz).DefaultIfEmpty(current.RefreshHz).Max(),
                    MaxRefreshHzAnyResolution = ratesByMode.Select(m => m.MaxHz).DefaultIfEmpty(current.RefreshHz).Max(),
                });
            }

            name = null;
            current = null;
            ratesByMode.Clear();
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith("Screen ", StringComparison.Ordinal))
                continue;

            if (!char.IsWhiteSpace(line[0]))
            {
                Flush();
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[1] == "connected")
                {
                    name = parts[0];
                    primary = parts.Contains("primary");
                }

                continue;
            }

            if (name is null)
                continue;

            // "   2560x1440    164.96*+ 143.97   59.95"
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var size = tokens[0].Split('x');
            if (size.Length != 2 || !int.TryParse(size[0], out var w) || !int.TryParse(new string(size[1].TakeWhile(char.IsDigit).ToArray()), out var h))
                continue;

            var max = 0;
            foreach (var token in tokens.Skip(1))
            {
                var number = token.TrimEnd('*', '+');
                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz))
                    continue;
                var rounded = (int)Math.Round(hz);
                max = Math.Max(max, rounded);
                if (token.Contains('*'))
                    current = new DisplayMode(w, h, rounded);
            }

            ratesByMode.Add((w, h, max));
        }

        Flush();
        return result;
    }
}
