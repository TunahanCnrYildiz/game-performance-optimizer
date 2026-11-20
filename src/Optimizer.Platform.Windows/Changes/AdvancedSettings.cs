using Microsoft.Win32;
using Optimizer.Core.Changes;
using Optimizer.Core.Inventory;

namespace Optimizer.Platform.Windows.Changes;

public sealed record HvciState(Toggle Configured, Toggle Running, bool Locked, bool PolicyManaged)
{
    /// <summary>A UEFI lock or a Group Policy overrides the registry value; changing it would do nothing.</summary>
    public bool CanChange => !Locked && !PolicyManaged;
}

/// <summary>
/// Reads current state and builds changes for the administrator-level settings of the
/// Advanced tab. All values here are also on <see cref="Elevation.ElevatedChangePolicy"/>'s allowlist.
/// </summary>
public static class AdvancedSettings
{
    public const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    public const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string HvciPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard";

    public static readonly ManualTweak HagsOn = new("advanced.hags.on", "Donanım hızlandırmalı GPU zamanlamasını aç", Risk.Low, RequiresAdmin: true, RequiresRestart: true);
    public static readonly ManualTweak HagsOff = new("advanced.hags.off", "Donanım hızlandırmalı GPU zamanlamasını kapat", Risk.Low, RequiresAdmin: true, RequiresRestart: true);
    public static readonly ManualTweak HvciOff = new("advanced.hvci.off", "Bellek Bütünlüğü'nü (HVCI) kapat", Risk.High, RequiresAdmin: true, RequiresRestart: true);
    public static readonly ManualTweak HvciOn = new("advanced.hvci.on", "Bellek Bütünlüğü'nü (HVCI) aç", Risk.Low, RequiresAdmin: true, RequiresRestart: true);
    public static readonly ManualTweak DefenderExclusion = new("advanced.defender.game-folders", "Oyun klasörlerini Defender taramasından hariç tut", Risk.Medium, RequiresAdmin: true);

    public static ManualTweak StartupToggle(StartupItem item, bool enable) => new(
        $"advanced.startup.{(enable ? "enable" : "disable")}.{item.Name}",
        $"Başlangıç programı: {item.Name} {(enable ? "etkinleştir" : "devre dışı bırak")}",
        Risk.Low,
        RequiresAdmin: item.ApprovedHive == "HKLM");

    public static Toggle Hags()
    {
        using var key = Registry.LocalMachine.OpenSubKey(GraphicsDriversKey);
        return key?.GetValue("HwSchMode") switch
        {
            2 => Toggle.On,
            1 => Toggle.Off,
            _ => Toggle.Unknown,
        };
    }

    public static Change HagsChange(bool on) =>
        new RegistryValueChange("HKLM", GraphicsDriversKey, "HwSchMode", null, RegistryData.Dword(on ? 2 : 1));

    public static HvciState Hvci(Toggle running)
    {
        using var key = Registry.LocalMachine.OpenSubKey(HvciKey);
        using var policy = Registry.LocalMachine.OpenSubKey(HvciPolicyKey);
        var configured = key?.GetValue("Enabled") switch
        {
            1 => Toggle.On,
            0 => Toggle.Off,
            _ => Toggle.Unknown,
        };
        return new HvciState(
            configured,
            running,
            Locked: key?.GetValue("Locked") is 1,
            PolicyManaged: policy?.GetValue("HypervisorEnforcedCodeIntegrity") is not null);
    }

    /// <summary>Configured value, lock/policy, and whether it is actually running right now (WMI).</summary>
    public static HvciState ReadHvci() => Hvci(Collectors.WmiCollectors.CollectSecurity().MemoryIntegrity);

    public static IReadOnlyList<StartupItem> ReadStartupItems() => Collectors.RegistryCollectors.CollectStartupItems();

    public static Change HvciChange(bool on) =>
        new RegistryValueChange("HKLM", HvciKey, "Enabled", null, RegistryData.Dword(on ? 1 : 0));

    /// <summary>Task Manager's 12-byte state: first byte 02 = enabled, 03 = disabled; the rest is a timestamp we leave zero.</summary>
    public static Change StartupChange(StartupItem item, bool enable) =>
        new RegistryValueChange(
            item.ApprovedHive,
            item.ApprovedKey,
            item.ApprovedName,
            null,
            new RegistryData(RegistryDataKind.Binary, (enable ? "02" : "03") + new string('0', 22)));

    public static IReadOnlyList<Change> DefenderChanges(IEnumerable<string> folders) =>
        folders.Select(f => (Change)new DefenderExclusionChange(f, null)).ToList();
}
