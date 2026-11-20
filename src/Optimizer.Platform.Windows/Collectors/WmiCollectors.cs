using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Optimizer.Core.Inventory;

namespace Optimizer.Platform.Windows.Collectors;

internal static partial class WmiCollectors
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static OsInfo CollectOs()
    {
        using var os = QueryOne("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem");
        using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

        var displayVersion = cv?.GetValue("DisplayVersion") as string;
        var ubr = cv?.GetValue("UBR") is int u ? u : 0;
        var build = int.TryParse(os?["BuildNumber"] as string, out var b) ? b : Environment.OSVersion.Version.Build;

        return new OsInfo(
            Name: ((os?["Caption"] as string)?.Trim() ?? "Windows") + (displayVersion is null ? "" : $" {displayVersion}"),
            Version: ubr > 0 ? $"{build}.{ubr}" : build.ToString(),
            Build: build,
            Architecture: os?["OSArchitecture"] as string ?? Environment.Is64BitOperatingSystem.ToString());
    }

    public static CpuInfo CollectCpu()
    {
        var processors = Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        try
        {
            var first = processors.FirstOrDefault();
            return new CpuInfo(
                Name: (first?["Name"] as string)?.Trim() ?? "Bilinmeyen işlemci",
                PhysicalCores: processors.Sum(p => ToInt(p["NumberOfCores"])),
                LogicalProcessors: processors.Sum(p => ToInt(p["NumberOfLogicalProcessors"])),
                MaxClockMhz: processors.Select(p => ToInt(p["MaxClockSpeed"])).DefaultIfEmpty().Max());
        }
        finally
        {
            Dispose(processors);
        }
    }

    public static IReadOnlyList<GpuInfo> CollectGpus()
    {
        var controllers = Query("SELECT Name, DriverVersion, DriverDate, PNPDeviceID FROM Win32_VideoController");
        try
        {
            var memoryByName = ReadAdapterMemoryFromRegistry();

            return controllers.Select(c =>
            {
                var name = (c["Name"] as string)?.Trim() ?? "Bilinmeyen ekran kartı";
                var pnp = c["PNPDeviceID"] as string;
                memoryByName.TryGetValue(name, out var memory);
                var vendor = DetectVendor(pnp);

                return new GpuInfo
                {
                    Name = name,
                    Vendor = vendor,
                    Kind = DetectKind(name, vendor, pnp, memory),
                    DriverVersion = c["DriverVersion"] as string,
                    DriverDate = ParseCimDate(c["DriverDate"] as string),
                    DedicatedMemoryBytes = memory > 0 ? memory : null,
                    PnpDeviceId = pnp,
                };
            }).ToList();
        }
        finally
        {
            Dispose(controllers);
        }
    }

    public static MemoryInfo CollectMemory()
    {
        var modules = Query(
            "SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, Manufacturer, PartNumber, DeviceLocator FROM Win32_PhysicalMemory");
        try
        {
            var list = modules.Select(m => new MemoryModule(
                CapacityBytes: ToLong(m["Capacity"]),
                RatedSpeedMts: ToInt(m["Speed"]),
                ConfiguredSpeedMts: ToInt(m["ConfiguredClockSpeed"]),
                Generation: ToInt(m["SMBIOSMemoryType"]) switch
                {
                    24 => MemoryGeneration.Ddr3,
                    26 => MemoryGeneration.Ddr4,
                    30 => MemoryGeneration.Lpddr4,
                    34 => MemoryGeneration.Ddr5,
                    35 => MemoryGeneration.Lpddr5,
                    _ => MemoryGeneration.Unknown,
                },
                Manufacturer: (m["Manufacturer"] as string)?.Trim(),
                PartNumber: (m["PartNumber"] as string)?.Trim(),
                Slot: (m["DeviceLocator"] as string)?.Trim())).ToList();

            var total = list.Sum(m => m.CapacityBytes);
            if (total <= 0)
                total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

            return new MemoryInfo(total, list);
        }
        finally
        {
            Dispose(modules);
        }
    }

    public static SecurityInfo CollectSecurity()
    {
        var vbs = Toggle.Unknown;
        var hvci = Toggle.Unknown;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\DeviceGuard",
                "SELECT VirtualizationBasedSecurityStatus, SecurityServicesRunning FROM Win32_DeviceGuard");
            using var results = searcher.Get();
            foreach (var mo in results.Cast<ManagementBaseObject>())
            {
                using (mo)
                {
                    vbs = ToInt(mo["VirtualizationBasedSecurityStatus"]) == 2 ? Toggle.On : Toggle.Off;
                    var running = mo["SecurityServicesRunning"] as uint[] ?? [];
                    hvci = running.Contains(2u) ? Toggle.On : Toggle.Off;
                }
            }
        }
        catch (ManagementException)
        {
            // Namespace missing on some editions; leave as unknown.
        }

        using var secureBootKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        var secureBoot = secureBootKey?.GetValue("UEFISecureBootEnabled") switch
        {
            1 => Toggle.On,
            0 => Toggle.Off,
            _ => Toggle.Unknown,
        };

        return new SecurityInfo { VirtualizationBasedSecurity = vbs, MemoryIntegrity = hvci, SecureBoot = secureBoot };
    }

    /// <summary>Maps drive letters ("C") to physical media type using the Storage WMI provider.</summary>
    public static Dictionary<char, (DiskMediaType Media, string? Bus)> CollectDriveMedia()
    {
        var map = new Dictionary<char, (DiskMediaType, string?)>();
        try
        {
            var disks = new Dictionary<uint, (DiskMediaType, string?)>();
            using (var searcher = new ManagementObjectSearcher(
                       @"root\Microsoft\Windows\Storage", "SELECT DeviceId, MediaType, BusType FROM MSFT_PhysicalDisk"))
            using (var results = searcher.Get())
            {
                foreach (var mo in results.Cast<ManagementBaseObject>())
                {
                    using (mo)
                    {
                        if (!uint.TryParse(mo["DeviceId"] as string, out var id))
                            continue;

                        var media = ToInt(mo["MediaType"]) switch
                        {
                            3 => DiskMediaType.Hdd,
                            4 => DiskMediaType.Ssd,
                            _ => DiskMediaType.Unknown,
                        };
                        var bus = ToInt(mo["BusType"]) switch
                        {
                            7 => "USB",
                            8 => "RAID",
                            11 => "SATA",
                            17 => "NVMe",
                            _ => null,
                        };
                        // NVMe drives are always solid state even when MediaType is unspecified.
                        if (media == DiskMediaType.Unknown && bus == "NVMe")
                            media = DiskMediaType.Ssd;

                        disks[id] = (media, bus);
                    }
                }
            }

            using var partSearcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage", "SELECT DriveLetter, DiskNumber FROM MSFT_Partition");
            using var parts = partSearcher.Get();
            foreach (var mo in parts.Cast<ManagementBaseObject>())
            {
                using (mo)
                {
                    var letter = mo["DriveLetter"] is char c ? c : (char)ToInt(mo["DriveLetter"]);
                    if (!char.IsLetter(letter))
                        continue;

                    if (disks.TryGetValue((uint)ToInt(mo["DiskNumber"]), out var info))
                        map[char.ToUpperInvariant(letter)] = info;
                }
            }
        }
        catch (ManagementException)
        {
            // Storage provider unavailable; media types stay unknown.
        }

        return map;
    }

    // ---------------- helpers ----------------

    private static Dictionary<string, long> ReadAdapterMemoryFromRegistry()
    {
        // Win32_VideoController.AdapterRAM is a uint32 and caps at 4 GB, so read the driver's QWORD instead.
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        if (classKey is null)
            return result;

        foreach (var sub in classKey.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var key = classKey.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc)
                    continue;

                long size = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] { Length: >= 8 } bytes => BitConverter.ToInt64(bytes, 0),
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] { Length: >= 4 } bytes => BitConverter.ToUInt32(bytes, 0),
                        _ => 0,
                    },
                };

                if (size > 0 && (!result.TryGetValue(desc, out var existing) || size > existing))
                    result[desc] = size;
            }
            catch (System.Security.SecurityException)
            {
                // Some subkeys (e.g. "Properties") are not readable without admin.
            }
        }

        return result;
    }

    private static GpuVendor DetectVendor(string? pnp)
    {
        if (pnp is null || !pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
            return GpuVendor.Virtual;

        var match = VendorIdRegex().Match(pnp);
        return match.Success
            ? match.Groups[1].Value.ToUpperInvariant() switch
            {
                "10DE" => GpuVendor.Nvidia,
                "1002" or "1022" => GpuVendor.Amd,
                "8086" => GpuVendor.Intel,
                "5143" or "17CB" => GpuVendor.Qualcomm,
                _ => GpuVendor.Unknown,
            }
            : GpuVendor.Unknown;
    }

    /// <summary>Heuristic; Windows exposes no reliable "is integrated" flag without D3D.</summary>
    internal static GpuKind DetectKind(string name, GpuVendor vendor, string? pnp, long memoryBytes)
    {
        if (vendor == GpuVendor.Virtual)
            return GpuKind.Virtual;

        const long OneGiB = 1024L * 1024 * 1024;

        return vendor switch
        {
            GpuVendor.Nvidia => GpuKind.Discrete,
            GpuVendor.Qualcomm => GpuKind.Integrated,
            GpuVendor.Intel => IntelDiscreteRegex().IsMatch(name) ? GpuKind.Discrete : GpuKind.Integrated,
            GpuVendor.Amd when AmdIntegratedRegex().IsMatch(name) => GpuKind.Integrated,
            GpuVendor.Amd when memoryBytes is > 0 and < OneGiB => GpuKind.Integrated,
            GpuVendor.Amd => GpuKind.Discrete,
            _ => GpuKind.Unknown,
        };
    }

    internal static DateOnly? ParseCimDate(string? cim)
    {
        // CIM_DATETIME: yyyymmddHHMMSS.mmmmmmsUUU
        if (cim is null || cim.Length < 8)
            return null;

        return int.TryParse(cim[..4], out var y) && int.TryParse(cim[4..6], out var m) && int.TryParse(cim[6..8], out var d)
               && y > 1990 && m is >= 1 and <= 12 && d is >= 1 and <= 31
            ? new DateOnly(y, m, d)
            : null;
    }

    private static ManagementBaseObject? QueryOne(string wql) => Query(wql).FirstOrDefault();

    private static List<ManagementBaseObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        using var results = searcher.Get();
        return results.Cast<ManagementBaseObject>().ToList();
    }

    private static void Dispose(IEnumerable<ManagementBaseObject> objects)
    {
        foreach (var o in objects)
            o.Dispose();
    }

    private static int ToInt(object? value) => value is null ? 0 : Convert.ToInt32(value);

    private static long ToLong(object? value) => value is null ? 0 : Convert.ToInt64(value);

    [GeneratedRegex(@"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VendorIdRegex();

    // Arc A-series / B-series desktop and laptop dGPUs (A770, A370M, B580). Lunar Lake "Arc 140V" is integrated.
    [GeneratedRegex(@"Arc\(TM\)\s+[AB]\d{3}|Arc\s+[AB]\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex IntelDiscreteRegex();

    [GeneratedRegex(@"Radeon\(TM\)\s+Graphics|Radeon\s+Graphics|Radeon\s+\d{3,4}M\b|Radeon\(TM\)\s+\d{3,4}M\b|Vega\s+\d+\s+Graphics", RegexOptions.IgnoreCase)]
    private static partial Regex AmdIntegratedRegex();
}
