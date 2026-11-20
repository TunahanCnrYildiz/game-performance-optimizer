using Optimizer.Core.Inventory;
using static Optimizer.Platform.Windows.Interop.NativeMethods;

namespace Optimizer.Platform.Windows.Collectors;

internal static class DisplayCollector
{
    public static IReadOnlyList<DisplayInfo> Collect()
    {
        var friendlyNames = TryGetMonitorFriendlyNames();
        var result = new List<DisplayInfo>();

        for (uint i = 0; ; i++)
        {
            var adapter = DISPLAY_DEVICE.Create();
            if (!EnumDisplayDevices(null, i, ref adapter, 0))
                break;

            if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0
                || (adapter.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                continue;

            var current = DEVMODE.Create();
            if (!EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref current))
                continue;

            var (maxAtRes, maxAny) = ScanModes(adapter.DeviceName, current.dmPelsWidth, current.dmPelsHeight);

            friendlyNames.TryGetValue(adapter.DeviceName, out var monitorName);
            if (string.IsNullOrWhiteSpace(monitorName))
            {
                var monitor = DISPLAY_DEVICE.Create();
                if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0))
                    monitorName = monitor.DeviceString;
            }

            result.Add(new DisplayInfo
            {
                DeviceName = adapter.DeviceName,
                MonitorName = monitorName,
                AdapterName = adapter.DeviceString,
                IsPrimary = (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                Current = new DisplayMode(current.dmPelsWidth, current.dmPelsHeight, current.dmDisplayFrequency),
                MaxRefreshHzAtCurrentResolution = Math.Max(maxAtRes, current.dmDisplayFrequency),
                MaxRefreshHzAnyResolution = Math.Max(maxAny, current.dmDisplayFrequency),
            });
        }

        return result;
    }

    private static (int MaxAtResolution, int MaxAny) ScanModes(string deviceName, int width, int height)
    {
        int maxAtRes = 0, maxAny = 0;
        var mode = DEVMODE.Create();

        for (var n = 0; EnumDisplaySettings(deviceName, n, ref mode); n++)
        {
            // 1 Hz entries mean "hardware default" on some drivers.
            if (mode.dmDisplayFrequency <= 1)
                continue;

            maxAny = Math.Max(maxAny, mode.dmDisplayFrequency);
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height)
                maxAtRes = Math.Max(maxAtRes, mode.dmDisplayFrequency);
        }

        return (maxAtRes, maxAny);
    }

    /// <summary>Maps GDI device names (\\.\DISPLAY1) to EDID monitor names ("LG ULTRAGEAR").</summary>
    private static Dictionary<string, string> TryGetMonitorFriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out var pathCount, out var modeCount) != 0)
                return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return map;

            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id,
                    },
                };

                if (DisplayConfigGetDeviceInfo(ref source) == 0
                    && DisplayConfigGetDeviceInfo(ref target) == 0
                    && !string.IsNullOrWhiteSpace(target.monitorFriendlyDeviceName))
                {
                    map[source.viewGdiDeviceName] = target.monitorFriendlyDeviceName;
                }
            }
        }
        catch (Exception)
        {
            // Friendly names are cosmetic; fall back to the generic PnP name.
        }

        return map;
    }
}
