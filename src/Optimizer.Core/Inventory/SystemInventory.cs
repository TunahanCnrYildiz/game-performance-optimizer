namespace Optimizer.Core.Inventory;

/// <summary>
/// Read-only snapshot of everything the diagnostic rules need. Every section is
/// nullable because a collector can fail on a given machine; rules must treat a
/// missing section as "unknown", never as "bad".
/// </summary>
public sealed record SystemInventory
{
    public required DateTimeOffset CollectedAt { get; init; }
    public OsInfo? Os { get; init; }
    public CpuInfo? Cpu { get; init; }
    public IReadOnlyList<GpuInfo> Gpus { get; init; } = [];
    public MemoryInfo? Memory { get; init; }
    public IReadOnlyList<DisplayInfo> Displays { get; init; } = [];
    public PowerInfo? Power { get; init; }
    public GamingSettings? Gaming { get; init; }
    public SecurityInfo? Security { get; init; }
    public IReadOnlyList<DiskInfo> Disks { get; init; } = [];
    public IReadOnlyList<StartupItem> StartupItems { get; init; } = [];

    /// <summary>Collectors that failed, so the UI can say what could not be checked.</summary>
    public IReadOnlyList<CollectorError> Errors { get; init; } = [];

    /// <summary>True when the machine has a battery (laptop, handheld, tablet).</summary>
    public bool IsPortable => Power?.HasBattery == true;
}

public sealed record CollectorError(string Collector, string Message);

public sealed record OsInfo(string Name, string Version, int Build, string Architecture);

public sealed record CpuInfo(string Name, int PhysicalCores, int LogicalProcessors, int MaxClockMhz);

public enum GpuVendor { Unknown, Nvidia, Amd, Intel, Qualcomm, Virtual }

public enum GpuKind { Unknown, Integrated, Discrete, Virtual }

public sealed record GpuInfo
{
    public required string Name { get; init; }
    public GpuVendor Vendor { get; init; }
    public GpuKind Kind { get; init; }
    public string? DriverVersion { get; init; }
    public DateOnly? DriverDate { get; init; }
    public long? DedicatedMemoryBytes { get; init; }
    public string? PnpDeviceId { get; init; }
}

public enum MemoryGeneration { Unknown, Ddr3, Ddr4, Ddr5, Lpddr4, Lpddr5 }

public sealed record MemoryModule(
    long CapacityBytes,
    int RatedSpeedMts,
    int ConfiguredSpeedMts,
    MemoryGeneration Generation,
    string? Manufacturer,
    string? PartNumber,
    string? Slot);

public sealed record MemoryInfo(long TotalBytes, IReadOnlyList<MemoryModule> Modules)
{
    public MemoryGeneration Generation =>
        Modules.Select(m => m.Generation).FirstOrDefault(g => g != MemoryGeneration.Unknown);

    public int ConfiguredSpeedMts => Modules.Count == 0 ? 0 : Modules.Min(m => m.ConfiguredSpeedMts);
}

public sealed record DisplayMode(int Width, int Height, int RefreshHz);

public sealed record DisplayInfo
{
    public required string DeviceName { get; init; }
    public string? MonitorName { get; init; }

    /// <summary>Name of the graphics adapter this display is driven by.</summary>
    public string? AdapterName { get; init; }

    public bool IsPrimary { get; init; }
    public required DisplayMode Current { get; init; }

    /// <summary>Highest refresh rate the display offers at the current resolution.</summary>
    public int MaxRefreshHzAtCurrentResolution { get; init; }

    /// <summary>Highest refresh rate offered at any resolution.</summary>
    public int MaxRefreshHzAnyResolution { get; init; }
}

public enum PowerModeOverlay { Unknown, BestPowerEfficiency, Balanced, BestPerformance }

public sealed record PowerInfo
{
    public bool HasBattery { get; init; }
    public bool OnAcPower { get; init; }
    public int? BatteryPercent { get; init; }
    public bool BatterySaverOn { get; init; }
    public Guid? ActiveSchemeGuid { get; init; }
    public string? ActiveSchemeName { get; init; }
    public PowerModeOverlay OverlayOnAc { get; init; }
    public PowerModeOverlay OverlayOnBattery { get; init; }

    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
}

public enum Toggle { Unknown, Off, On }

public sealed record GpuPreferenceEntry(string ExecutablePath, int Preference);

public sealed record GamingSettings
{
    public Toggle GameMode { get; init; }

    /// <summary>"Record what happened" — continuous background recording.</summary>
    public Toggle BackgroundRecording { get; init; }

    public Toggle GameDvrCapture { get; init; }

    /// <summary>Hardware-accelerated GPU scheduling.</summary>
    public Toggle Hags { get; init; }

    public Toggle WindowedGameOptimizations { get; init; }
    public Toggle VrrOptimizations { get; init; }
    public Toggle EnhancePointerPrecision { get; init; }
    public IReadOnlyList<GpuPreferenceEntry> GpuPreferences { get; init; } = [];
}

public sealed record SecurityInfo
{
    public Toggle VirtualizationBasedSecurity { get; init; }

    /// <summary>Memory Integrity / Hypervisor-protected Code Integrity, actually running.</summary>
    public Toggle MemoryIntegrity { get; init; }

    public Toggle SecureBoot { get; init; }
}

public enum DiskMediaType { Unknown, Hdd, Ssd }

public sealed record DiskInfo
{
    public required string RootPath { get; init; }
    public string? Label { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public bool IsSystemDrive { get; init; }
    public DiskMediaType MediaType { get; init; }
    public string? BusType { get; init; }

    /// <summary>Game library folders found on this drive (Steam etc.).</summary>
    public IReadOnlyList<string> GameLibraries { get; init; } = [];

    public double FreeRatio => TotalBytes <= 0 ? 0 : (double)FreeBytes / TotalBytes;
}

public sealed record StartupItem(string Name, string Command, string Location, bool Enabled);
