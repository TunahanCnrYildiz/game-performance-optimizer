using Optimizer.Core.Diagnostics;
using Optimizer.Core.Diagnostics.Rules;
using Optimizer.Core.Inventory;

namespace Optimizer.Core.Tests;

public class RuleTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static SystemInventory Desktop() => new()
    {
        CollectedAt = Now,
        Power = new PowerInfo { HasBattery = false, OnAcPower = true, ActiveSchemeGuid = PowerInfo.Balanced, ActiveSchemeName = "Dengeli" },
    };

    private static SystemInventory Laptop(bool onAc = true) => new()
    {
        CollectedAt = Now,
        Power = new PowerInfo { HasBattery = true, OnAcPower = onAc, ActiveSchemeGuid = PowerInfo.Balanced },
    };

    private static DisplayInfo Display(int current, int maxAtRes, int maxAny, string? adapter = null) => new()
    {
        DeviceName = @"\\.\DISPLAY1",
        MonitorName = "Test Monitor",
        AdapterName = adapter,
        IsPrimary = true,
        Current = new DisplayMode(2560, 1440, current),
        MaxRefreshHzAtCurrentResolution = maxAtRes,
        MaxRefreshHzAnyResolution = maxAny,
    };

    private static GpuInfo Igpu => new() { Name = "AMD Radeon(TM) Graphics", Vendor = GpuVendor.Amd, Kind = GpuKind.Integrated };
    private static GpuInfo Dgpu => new() { Name = "AMD Radeon RX 9070 XT", Vendor = GpuVendor.Amd, Kind = GpuKind.Discrete };

    private static MemoryInfo Memory(MemoryGeneration gen, int configured, int modules = 2, long perModule = 16 * GiB, int rated = 4800) =>
        new(perModule * modules,
            Enumerable.Range(0, modules)
                .Select(_ => new MemoryModule(perModule, rated, configured, gen, null, null, null))
                .ToList());

    // ---- Refresh rate ----

    [Fact]
    public void RefreshRate_below_supported_is_high_impact_warning()
    {
        var inv = Desktop() with { Displays = [Display(current: 60, maxAtRes: 165, maxAny: 165)] };

        var finding = Assert.Single(new RefreshRateRule().Evaluate(inv));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(Impact.High, finding.Impact);
        Assert.Contains("165", finding.Title);
    }

    [Fact]
    public void RefreshRate_tolerates_59_vs_60()
    {
        var inv = Desktop() with { Displays = [Display(current: 59, maxAtRes: 60, maxAny: 60)] };

        Assert.Equal(Severity.Ok, Assert.Single(new RefreshRateRule().Evaluate(inv)).Severity);
    }

    [Fact]
    public void RefreshRate_higher_only_at_other_resolution_is_info_about_cable()
    {
        var inv = Desktop() with { Displays = [Display(current: 60, maxAtRes: 60, maxAny: 144)] };

        var finding = Assert.Single(new RefreshRateRule().Evaluate(inv));

        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void RefreshRate_60hz_monitor_with_legacy_75hz_modes_is_ok()
    {
        var inv = Desktop() with { Displays = [Display(current: 59, maxAtRes: 60, maxAny: 75)] };

        Assert.Equal(Severity.Ok, Assert.Single(new RefreshRateRule().Evaluate(inv)).Severity);
    }

    // ---- Monitor plugged into motherboard ----

    [Fact]
    public void Desktop_monitor_on_igpu_with_dgpu_present_warns()
    {
        var inv = Desktop() with
        {
            Gpus = [Igpu, Dgpu],
            Displays = [Display(165, 165, 165, adapter: Igpu.Name)],
        };

        var finding = Assert.Single(new MonitorOnIntegratedGpuRule().Evaluate(inv));

        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void Desktop_monitor_on_dgpu_is_ok()
    {
        var inv = Desktop() with
        {
            Gpus = [Igpu, Dgpu],
            Displays = [Display(165, 165, 165, adapter: Dgpu.Name)],
        };

        Assert.Equal(Severity.Ok, Assert.Single(new MonitorOnIntegratedGpuRule().Evaluate(inv)).Severity);
    }

    [Fact]
    public void Laptop_monitor_on_igpu_is_not_flagged()
    {
        var inv = Laptop() with
        {
            Gpus = [Igpu, Dgpu],
            Displays = [Display(165, 165, 165, adapter: Igpu.Name)],
        };

        Assert.Empty(new MonitorOnIntegratedGpuRule().Evaluate(inv));
    }

    [Fact]
    public void Hybrid_rule_only_for_laptops()
    {
        Assert.Empty(new HybridGpuRule().Evaluate(Desktop() with { Gpus = [Igpu, Dgpu] }));
        Assert.Single(new HybridGpuRule().Evaluate(Laptop() with { Gpus = [Igpu, Dgpu] }));
    }

    // ---- Driver age ----

    [Theory]
    [InlineData(30, Severity.Ok)]
    [InlineData(200, Severity.Info)]
    [InlineData(400, Severity.Suggestion)]
    public void Driver_age_thresholds(int ageDays, Severity expected)
    {
        var date = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-ageDays);
        var inv = Desktop() with { Gpus = [Igpu with { DriverDate = date }, Dgpu with { DriverDate = date }] };

        var finding = Assert.Single(new GpuDriverAgeRule().Evaluate(inv));

        Assert.Equal(expected, finding.Severity);
        Assert.Contains(Dgpu.Name, finding.Title);
    }

    [Fact]
    public void Driver_age_ignores_virtual_adapters()
    {
        var old = DateOnly.FromDateTime(Now.UtcDateTime).AddYears(-3);
        var inv = Desktop() with { Gpus = [new GpuInfo { Name = "Virtual", Kind = GpuKind.Virtual, DriverDate = old }] };

        Assert.Empty(new GpuDriverAgeRule().Evaluate(inv));
    }

    // ---- Memory ----

    [Fact]
    public void Ddr5_at_jedec_speed_on_desktop_suggests_expo()
    {
        var inv = Desktop() with { Memory = Memory(MemoryGeneration.Ddr5, configured: 4800) };

        Assert.Equal(Severity.Suggestion, Assert.Single(new MemorySpeedRule().Evaluate(inv)).Severity);
    }

    [Fact]
    public void Ddr5_with_expo_is_ok()
    {
        var inv = Desktop() with { Memory = Memory(MemoryGeneration.Ddr5, configured: 6000) };

        Assert.Equal(Severity.Ok, Assert.Single(new MemorySpeedRule().Evaluate(inv)).Severity);
    }

    [Fact]
    public void Ddr5_at_its_own_jedec_5600_is_info_not_a_claim_about_expo()
    {
        var inv = Desktop() with { Memory = Memory(MemoryGeneration.Ddr5, configured: 5600, rated: 5600) };

        var finding = Assert.Single(new MemorySpeedRule().Evaluate(inv));

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.DoesNotContain("etkin", finding.Title);
    }

    // ---- HAGS ----

    [Fact]
    public void Hags_off_only_suggested_for_nvidia_frame_generation()
    {
        var off = new GamingSettings { Hags = Toggle.Off };
        var nvidia = new GpuInfo { Name = "NVIDIA GeForce RTX 5080", Vendor = GpuVendor.Nvidia, Kind = GpuKind.Discrete };

        Assert.Empty(new HagsRule().Evaluate(Desktop() with { Gaming = off, Gpus = [Dgpu] }));
        Assert.Equal(Severity.Suggestion, Assert.Single(new HagsRule().Evaluate(Desktop() with { Gaming = off, Gpus = [nvidia] })).Severity);
    }

    [Fact]
    public void Memory_speed_skipped_on_laptops()
    {
        var inv = Laptop() with { Memory = Memory(MemoryGeneration.Ddr5, configured: 4800) };

        Assert.Empty(new MemorySpeedRule().Evaluate(inv));
    }

    [Fact]
    public void Single_dimm_flags_single_channel()
    {
        var inv = Desktop() with { Memory = Memory(MemoryGeneration.Ddr4, 3200, modules: 1, perModule: 16 * GiB) };

        var findings = new MemoryChannelRule().Evaluate(inv).ToList();

        Assert.Contains(findings, f => f.Id == "memory.layout.single-channel");
    }

    [Fact]
    public void Eight_gib_total_is_suggestion()
    {
        var inv = Desktop() with { Memory = Memory(MemoryGeneration.Ddr4, 3200, modules: 2, perModule: 4 * GiB) };

        var capacity = Assert.Single(new MemoryChannelRule().Evaluate(inv), f => f.Id == "memory.layout.capacity");

        Assert.Equal(Severity.Suggestion, capacity.Severity);
    }

    // ---- Power ----

    [Fact]
    public void Laptop_on_battery_is_flagged()
    {
        var findings = new PowerRule().Evaluate(Laptop(onAc: false)).ToList();

        Assert.Contains(findings, f => f.Id == "power.on-battery");
    }

    [Fact]
    public void Power_saver_scheme_is_warning()
    {
        var inv = Desktop() with { Power = Desktop().Power! with { ActiveSchemeGuid = PowerInfo.PowerSaver } };

        var finding = Assert.Single(new PowerRule().Evaluate(inv));

        Assert.Equal(Severity.Warning, finding.Severity);
    }

    [Fact]
    public void Healthy_power_returns_single_ok()
    {
        Assert.Equal(Severity.Ok, Assert.Single(new PowerRule().Evaluate(Desktop())).Severity);
    }

    // ---- Game Mode ----

    private static SystemInventory WithCpu(string cpu, Toggle gameMode) => Desktop() with
    {
        Cpu = new CpuInfo(cpu, 16, 32, 4300),
        Gaming = new GamingSettings { GameMode = gameMode },
    };

    [Theory]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor")]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor")]
    [InlineData("Intel(R) Core(TM) i7-14700K")]
    public void Game_mode_off_is_only_info_on_ordinary_cpus(string cpu)
    {
        var finding = Assert.Single(new GameModeRule().Evaluate(WithCpu(cpu, Toggle.Off)));

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal(Impact.None, finding.Impact);
    }

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor")]
    [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor")]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor")]
    public void Game_mode_off_on_dual_ccd_x3d_is_warning(string cpu)
    {
        var finding = Assert.Single(new GameModeRule().Evaluate(WithCpu(cpu, Toggle.Off)));

        Assert.Equal(Severity.Warning, finding.Severity);
    }

    // ---- Unknown data never produces problems ----

    [Fact]
    public void Empty_inventory_produces_no_problems()
    {
        var empty = new SystemInventory { CollectedAt = Now };

        var findings = DiagnosticEngine.CreateDefault().Evaluate(empty);

        Assert.DoesNotContain(findings, f => f.Severity >= Severity.Suggestion);
    }

    // ---- Storage ----

    [Fact]
    public void Steam_library_on_hdd_is_flagged()
    {
        var inv = Desktop() with
        {
            Disks =
            [
                new DiskInfo { RootPath = @"D:\", TotalBytes = 2000 * GiB, FreeBytes = 500 * GiB, MediaType = DiskMediaType.Hdd, GameLibraries = [@"D:\SteamLibrary"] },
            ],
        };

        Assert.Equal(Severity.Suggestion, Assert.Single(new GamesOnHddRule().Evaluate(inv)).Severity);
    }

    [Fact]
    public void Low_system_drive_space_warns()
    {
        var inv = Desktop() with
        {
            Disks = [new DiskInfo { RootPath = @"C:\", IsSystemDrive = true, TotalBytes = 500 * GiB, FreeBytes = 15 * GiB }],
        };

        Assert.Equal(Severity.Warning, Assert.Single(new DiskSpaceRule().Evaluate(inv)).Severity);
    }

    // ---- Engine ----

    private sealed class ThrowingRule : IRule
    {
        public string Id => "test.throws";
        public IEnumerable<Finding> Evaluate(SystemInventory inventory) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Engine_survives_a_throwing_rule_and_sorts_by_severity()
    {
        var engine = new DiagnosticEngine([new ThrowingRule(), new RefreshRateRule()]);
        var inv = Desktop() with { Displays = [Display(60, 165, 165)] };

        var findings = engine.Evaluate(inv);

        Assert.Equal(2, findings.Count);
        Assert.Equal(Severity.Warning, findings[0].Severity);
        Assert.Contains(findings, f => f.Id == "engine.rule-failed.test.throws");
    }
}
