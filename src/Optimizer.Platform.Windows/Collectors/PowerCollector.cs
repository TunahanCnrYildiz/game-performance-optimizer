using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Optimizer.Core.Inventory;
using static Optimizer.Platform.Windows.Interop.NativeMethods;

namespace Optimizer.Platform.Windows.Collectors;

internal static class PowerCollector
{
    private const string PowerSchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

    public static PowerInfo Collect()
    {
        GetSystemPowerStatus(out var status);

        // 128 = "no system battery", 255 = unknown.
        var hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255;
        var (schemeGuid, schemeName) = GetActiveScheme();

        using var key = Registry.LocalMachine.OpenSubKey(PowerSchemesKey);

        return new PowerInfo
        {
            HasBattery = hasBattery,
            OnAcPower = status.ACLineStatus == 1,
            BatteryPercent = hasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null,
            BatterySaverOn = (status.SystemStatusFlag & 1) != 0,
            ActiveSchemeGuid = schemeGuid,
            ActiveSchemeName = schemeName,
            OverlayOnAc = MapOverlay(key?.GetValue("ActiveOverlayAcPowerScheme") as string),
            OverlayOnBattery = MapOverlay(key?.GetValue("ActiveOverlayDcPowerScheme") as string),
        };
    }

    private static (Guid? Guid, string? Name) GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero)
            return (null, null);

        try
        {
            var guid = Marshal.PtrToStructure<Guid>(ptr);
            return (guid, ReadFriendlyName(guid));
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    private static string? ReadFriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0)
            return null;

        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
            return null;

        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    /// <summary>The Windows 10/11 "power mode" slider is stored as an overlay scheme GUID.</summary>
    private static PowerModeOverlay MapOverlay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value, out var guid) || guid == Guid.Empty)
            return PowerModeOverlay.Balanced;

        return guid.ToString().ToLowerInvariant() switch
        {
            "ded574b5-45a0-4f42-8737-46345c09c238" => PowerModeOverlay.BestPerformance,
            "961cc777-2547-4f9d-8174-7d86181b8a7a" => PowerModeOverlay.BestPowerEfficiency,
            "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => PowerModeOverlay.Balanced,
            _ => PowerModeOverlay.Unknown,
        };
    }
}
