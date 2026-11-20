using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Inventory;
using Optimizer.Core.Sessions;

namespace Optimizer.Platform.Linux.Tests;

/// <summary>A throw-away folder laid out like / with hand-written /proc and /sys files.</summary>
public sealed class FakeRoot : IDisposable
{
    public FakeRoot() => Directory.CreateDirectory(Root);

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "optimizer-linux-tests", Guid.NewGuid().ToString("N"));

    public LinuxFs Fs => new(Root);

    public FakeRoot With(string path, string content)
    {
        var full = Path.Combine(Root, path.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public string? Get(string path)
    {
        var full = Path.Combine(Root, path.TrimStart('/'));
        return File.Exists(full) ? File.ReadAllText(full) : null;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public sealed class LinuxInventoryTests : IDisposable
{
    private readonly FakeRoot _root = new();

    public void Dispose() => _root.Dispose();

    private LinuxInventoryCollector Collector(Dictionary<string, string>? env = null, Dictionary<string, string>? shell = null) =>
        new(_root.Fs, k => env?.GetValueOrDefault(k), (f, a) => shell?.GetValueOrDefault($"{f} {a}".Trim()));

    [Fact]
    public void Reads_os_cpu_and_memory()
    {
        _root.With("/etc/os-release", "NAME=\"Fedora Linux\"\nPRETTY_NAME=\"Fedora Linux 43 (Workstation Edition)\"\n")
            .With("/proc/sys/kernel/osrelease", "6.17.4-300.fc43.x86_64\n")
            .With("/proc/cpuinfo",
                "processor\t: 0\nmodel name\t: AMD Ryzen 7 9800X3D 8-Core Processor\nphysical id\t: 0\ncore id\t\t: 0\n\n"
                + "processor\t: 1\nmodel name\t: AMD Ryzen 7 9800X3D 8-Core Processor\nphysical id\t: 0\ncore id\t\t: 0\n\n"
                + "processor\t: 2\nmodel name\t: AMD Ryzen 7 9800X3D 8-Core Processor\nphysical id\t: 0\ncore id\t\t: 1\n\n")
            .With("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq", "5270000\n")
            .With("/proc/meminfo", "MemTotal:       32768000 kB\nMemFree:        1000 kB\n");

        var c = Collector();
        var os = c.CollectOs();
        var cpu = c.CollectCpu();

        Assert.Equal("Fedora Linux 43 (Workstation Edition)", os.Name);
        Assert.Equal("6.17.4-300.fc43.x86_64", os.Version);
        Assert.Equal("AMD Ryzen 7 9800X3D 8-Core Processor", cpu.Name);
        Assert.Equal(3, cpu.LogicalProcessors);
        Assert.Equal(2, cpu.PhysicalCores);
        Assert.Equal(5270, cpu.MaxClockMhz);
        Assert.Equal(32768000L * 1024, c.CollectMemory().TotalBytes);
    }

    [Fact]
    public void Reads_linux_tuning_state()
    {
        _root.With("/proc/sys/kernel/osrelease", "6.12.0\n")
            .With("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor", "powersave\n")
            .With("/sys/devices/system/cpu/cpu0/cpufreq/scaling_driver", "amd-pstate-epp\n")
            .With("/sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference", "balance_performance\n")
            .With("/proc/sys/vm/max_map_count", "65530\n")
            .With("/proc/sys/kernel/split_lock_mitigate", "1\n")
            .With("/usr/bin/gamemoded", "")
            .With("/usr/bin/pacman", "")
            .With("/sys/kernel/mm/transparent_hugepage/enabled", "always [madvise] never\n")
            .With("/sys/kernel/sched_ext/state", "enabled\n")
            .With("/sys/kernel/sched_ext/root/ops", "lavd\n");

        var linux = Collector(
            new() { ["XDG_SESSION_TYPE"] = "wayland" },
            new() { ["powerprofilesctl get"] = "balanced\n" }).CollectLinux();

        Assert.Equal("powersave", linux.CpuGovernor);
        Assert.Equal("amd-pstate-epp", linux.CpuFreqDriver);
        Assert.Equal(65530, linux.MaxMapCount);
        Assert.Equal(1, linux.SplitLockMitigate);
        Assert.True(linux.GameModeInstalled);
        Assert.Equal("pacman", linux.PackageManager);
        Assert.Equal("madvise", linux.TransparentHugePages);
        Assert.Equal("lavd", linux.SchedExtOps);
        Assert.Equal("balanced", linux.PowerProfile);
        Assert.Equal("wayland", linux.SessionType);
    }

    [Fact]
    public void Reads_battery_and_ac()
    {
        _root.With("/sys/class/power_supply/BAT0/type", "Battery\n")
            .With("/sys/class/power_supply/BAT0/capacity", "57\n")
            .With("/sys/class/power_supply/AC/type", "Mains\n")
            .With("/sys/class/power_supply/AC/online", "0\n")
            // A wireless mouse battery must not make a desktop look like a laptop.
            .With("/sys/class/power_supply/hidpp_battery_0/type", "Battery\n")
            .With("/sys/class/power_supply/hidpp_battery_0/scope", "Device\n");

        var power = Collector().CollectPower("power-saver");

        Assert.True(power.HasBattery);
        Assert.False(power.OnAcPower);
        Assert.Equal(57, power.BatteryPercent);
        Assert.Equal("power-saver", power.ActiveSchemeName);
    }

    [Fact]
    public void Single_file_bind_mounts_are_not_disks()
    {
        _root.With("/proc/mounts",
                "/dev/sdc / ext4 rw 0 0\n/dev/sdc /etc/resolv.conf ext4 rw 0 0\n/dev/nvme0n1p3 /home ext4 rw 0 0\nproc /proc proc rw 0 0\n")
            .With("/etc/resolv.conf", "nameserver 1.1.1.1\n")
            .With("/home/.keep", "")
            .With("/sys/block/nvme0n1/queue/rotational", "0\n")
            .With("/sys/block/sdc/queue/rotational", "1\n");

        var disks = Collector().CollectDisks();

        Assert.Equal(["/", "/home"], disks.Select(d => d.RootPath).ToArray());
        Assert.Equal(DiskMediaType.Ssd, disks.Single(d => d.RootPath == "/home").MediaType);
        Assert.Equal(DiskMediaType.Hdd, disks.Single(d => d.RootPath == "/").MediaType);
    }

    [Fact]
    public void Desktop_with_only_a_mouse_battery_is_not_portable()
    {
        _root.With("/sys/class/power_supply/hidpp_battery_0/type", "Battery\n")
            .With("/sys/class/power_supply/hidpp_battery_0/scope", "Device\n");

        Assert.False(Collector().CollectPower(null).HasBattery);
    }

    [Fact]
    public void Linux_rules_flag_low_map_count_power_saver_and_missing_gamemode()
    {
        var inventory = new SystemInventory
        {
            CollectedAt = DateTimeOffset.Now,
            Linux = new LinuxInfo
            {
                KernelVersion = "6.12",
                MaxMapCount = 65530,
                PowerProfile = "power-saver",
                GameModeInstalled = false,
                PackageManager = "apt",
                CpuGovernor = "powersave",
                CpuFreqDriver = "amd-pstate-epp",
                EnergyPerformancePreference = "balance_performance",
            },
        };

        var findings = DiagnosticEngine.CreateForLinux().Evaluate(inventory);

        Assert.Equal(Severity.Suggestion, findings.Single(f => f.Id == "linux.max-map-count").Severity);
        Assert.Equal(Severity.Warning, findings.Single(f => f.Id == "linux.power-profile").Severity);
        Assert.Contains("sudo apt install gamemode", findings.Single(f => f.Id == "linux.gamemode").Recommendation);
        // powersave with amd-pstate-epp is the normal mode, not a problem.
        Assert.Equal(Severity.Ok, findings.Single(f => f.Id == "linux.cpu-governor").Severity);
    }

    [Fact]
    public void Powersave_with_acpi_cpufreq_is_flagged()
    {
        var inventory = new SystemInventory
        {
            CollectedAt = DateTimeOffset.Now,
            Linux = new LinuxInfo { KernelVersion = "6.1", CpuGovernor = "powersave", CpuFreqDriver = "acpi-cpufreq" },
        };

        Assert.Equal(Severity.Suggestion, DiagnosticEngine.CreateForLinux().Evaluate(inventory).Single(f => f.Id == "linux.cpu-governor").Severity);
    }

    [Fact]
    public void Steam_libraries_include_extra_library_folders()
    {
        _root.With("/home/u/.local/share/Steam/steamapps/common/.keep", "")
            .With("/home/u/.local/share/Steam/steamapps/libraryfolders.vdf",
                "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"/home/u/.local/share/Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"/mnt/games/SteamLibrary\"\n\t}\n}\n")
            .With("/mnt/games/SteamLibrary/steamapps/common/.keep", "");

        var roots = SteamLibraries.FindGameRoots(_root.Fs, "/home/u");

        Assert.Contains("/home/u/.local/share/Steam/steamapps/common", roots);
        Assert.Contains("/mnt/games/SteamLibrary/steamapps/common", roots);
    }
}

public class ParserTests
{
    [Fact]
    public void Xrandr_current_and_max_rates()
    {
        const string output = """
            Screen 0: minimum 16 x 16, current 4480 x 1440, maximum 32767 x 32767
            DP-1 connected primary 2560x1440+0+0 (normal left inverted right x axis y axis) 597mm x 336mm
               2560x1440    59.95*+ 179.96   143.97
               1920x1080    60.00    50.00
            HDMI-A-1 connected 1920x1080+2560+0 (normal left inverted right x axis y axis) 477mm x 268mm
               1920x1080    60.00*+  59.94
               1024x768     75.03
            DP-2 disconnected (normal left inverted right x axis y axis)
            """;

        var displays = XrandrParser.Parse(output);

        Assert.Equal(2, displays.Count);
        var main = displays[0];
        Assert.True(main.IsPrimary);
        Assert.Equal(new DisplayMode(2560, 1440, 60), main.Current);
        Assert.Equal(180, main.MaxRefreshHzAtCurrentResolution);
        Assert.Equal(75, displays[1].MaxRefreshHzAnyResolution);
    }

    [Fact]
    public void MangoHud_log_parses_frames_and_game_name()
    {
        const string log = """
            os,cpu,gpu,ram,kernel,driver,cpuscheduler
            Arch Linux,AMD Ryzen 7 9800X3D,AMD Radeon RX 9070 XT,32 GB,6.17.4,Mesa 25.2.4,lavd
            fps,frametime,cpu_load,cpu_power,gpu_load,cpu_temp,gpu_temp,gpu_core_clock,gpu_mem_clock,gpu_vram_used,gpu_power,ram_used,swap_used,process_rss,cpu_mhz,elapsed
            144.1,6.94,35,40,97,61,70,2900,1250,9.1,250,12.3,0,4.2,5100,6940000
            143.8,6.95,35,40,97,61,70,2900,1250,9.1,250,12.3,0,4.2,5100,13890000
            """;
        var dir = Path.Combine(Path.GetTempPath(), "optimizer-linux-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "Cyberpunk2077.exe_2026-11-20_14-03-11.csv");
        File.WriteAllText(file, log);

        try
        {
            var (game, frames) = new MangoHudLog().Import(file);

            Assert.Equal("Cyberpunk2077.exe", game);
            Assert.Equal(2, frames.Count);
            Assert.Equal(6.94, frames[0].MsBetweenPresents, 6);
            Assert.Throws<InvalidDataException>(() => new MangoHudLog().Import(Path.Combine(dir, "x_2026-11-20_14-03-11_summary.csv")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Proc_stat_with_spaces_and_parens_in_name()
    {
        var parsed = LinuxSystemSampler.ParseStat(
            "4242 (Web Content (x)) S 1 4242 4242 0 -1 4194560 100 0 0 0 1500 250 0 0 20 0 30 0 987654 123456789 5000 18446744073709551615");

        Assert.NotNull(parsed);
        Assert.Equal("Web Content (x)", parsed!.Value.Comm);
        Assert.Equal(1500, parsed.Value.UTime);
        Assert.Equal(250, parsed.Value.STime);
        Assert.Equal(987654, parsed.Value.StartTicks);
        Assert.Equal(5000, parsed.Value.RssPages);
    }
}

public sealed class LinuxChangeTests : IDisposable
{
    private readonly FakeRoot _root = new();

    public void Dispose() => _root.Dispose();

    private sealed class FakeProfiles(string current) : IPowerProfiles
    {
        public string Current { get; private set; } = current;
        public string? Get() => Current;
        public IReadOnlyList<string> Available() => ["power-saver", "balanced", "performance"];
        public void Set(string profile) => Current = profile;
    }

    [Fact]
    public void Sysctl_apply_and_revert_restore_runtime_and_drop_in()
    {
        _root.With("/proc/sys/vm/max_map_count", "65530\n")
            .With("/proc/sys/kernel/split_lock_mitigate", "1\n")
            .With(LinuxChangeExecutor.DropInPath, "kernel.split_lock_mitigate = 0\n");
        var executor = new LinuxChangeExecutor(_root.Fs, new FakeProfiles("balanced"));
        var engine = new TweakEngine(executor, new Journal(Path.Combine(_root.Root, "journal")), "test");

        var result = engine.Apply(new ManualTweak("t", "t", Risk.Low), [new SysctlChange("vm.max_map_count", null, "1048576")]);

        Assert.True(result.Success, result.Error);
        Assert.Equal("1048576\n", _root.Get("/proc/sys/vm/max_map_count"));
        var dropIn = _root.Get(LinuxChangeExecutor.DropInPath)!;
        Assert.Contains("vm.max_map_count = 1048576", dropIn);
        Assert.Contains("kernel.split_lock_mitigate = 0", dropIn); // other key untouched

        engine.Revert(result.Entry!.Id);

        Assert.Equal("65530\n", _root.Get("/proc/sys/vm/max_map_count"));
        var after = _root.Get(LinuxChangeExecutor.DropInPath)!;
        Assert.DoesNotContain("max_map_count", after);
        Assert.Contains("kernel.split_lock_mitigate = 0", after);
    }

    [Fact]
    public void Our_drop_in_is_deleted_when_nothing_of_ours_is_left()
    {
        _root.With("/proc/sys/vm/max_map_count", "65530\n");
        var executor = new LinuxChangeExecutor(_root.Fs, new FakeProfiles("balanced"));
        var engine = new TweakEngine(executor, new Journal(Path.Combine(_root.Root, "journal")), "test");

        var result = engine.Apply(new ManualTweak("t", "t", Risk.Low), [new SysctlChange("vm.max_map_count", null, "1048576")]);
        engine.Revert(result.Entry!.Id);

        Assert.Null(_root.Get(LinuxChangeExecutor.DropInPath));
    }

    [Fact]
    public void Power_profile_round_trip()
    {
        var profiles = new FakeProfiles("power-saver");
        var engine = new TweakEngine(new LinuxChangeExecutor(_root.Fs, profiles), new Journal(Path.Combine(_root.Root, "journal")), "test");

        var result = engine.Apply(new ManualTweak("t", "t", Risk.Low), [new PowerProfileChange(null, "balanced")]);
        Assert.Equal("balanced", profiles.Current);

        engine.Revert(result.Entry!.Id);
        Assert.Equal("power-saver", profiles.Current);
    }

    [Theory]
    [InlineData("vm.max_map_count", "1048576", true)]
    [InlineData("vm.max_map_count", "999", false)]
    [InlineData("kernel.split_lock_mitigate", "0", true)]
    [InlineData("kernel.core_pattern", "|/tmp/evil", false)]
    [InlineData("kernel.randomize_va_space", "0", false)]
    [InlineData("vm.swappiness", "10", false)]
    public void Elevation_policy(string key, string value, bool allowed) =>
        Assert.Equal(allowed, new LinuxElevationPolicy().IsAllowed(new SysctlChange(key, "1", value)));

    [Fact]
    public void Policy_rejects_non_sysctl_and_forged_before_values()
    {
        var policy = new LinuxElevationPolicy();

        Assert.False(policy.IsAllowed(new PowerProfileChange(null, "performance")));
        Assert.False(policy.IsAllowed(new SysctlChange("vm.max_map_count", "1;rm -rf /", "1048576")));
    }
}

/// <summary>Runs only on a real Linux kernel (in CI or the Docker check); skipped elsewhere.</summary>
public class RealLinuxTests
{
    [Fact]
    public async Task Inventory_and_sampler_work_on_this_machine()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Yalnızca Linux'ta çalışır.");

        var inventory = await new LinuxInventoryCollector().CollectAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(inventory.Os);
        Assert.NotNull(inventory.Cpu);
        Assert.True(inventory.Cpu!.LogicalProcessors > 0);
        Assert.True(inventory.Memory!.TotalBytes > 0);
        Assert.NotNull(inventory.Linux?.MaxMapCount);

        using var sampler = new LinuxSystemSampler();
        sampler.Sample(detailed: true);
        Thread.Sleep(300);
        var sample = sampler.Sample(detailed: true);

        var self = Assert.Single(sample.Processes, p => p.Pid == Environment.ProcessId);
        Assert.True(self.WorkingSetBytes > 0);
        Assert.NotNull(self.Path);
        Assert.NotNull(sample.Cpu);
    }
}
