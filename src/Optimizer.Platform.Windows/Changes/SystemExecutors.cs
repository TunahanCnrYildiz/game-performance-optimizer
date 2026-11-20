using System.ComponentModel;
using System.Runtime.InteropServices;
using Optimizer.Core.Changes;
using Optimizer.Core.Inventory;
using static Optimizer.Platform.Windows.Interop.NativeMethods;

namespace Optimizer.Platform.Windows.Changes;

internal static class PowerSchemeExecutor
{
    public static PowerSchemeChange Capture(PowerSchemeChange desired) => desired with { Before = Current(), Captured = true };

    public static void Apply(PowerSchemeChange change) => Set(change.After);

    public static void Revert(PowerSchemeChange change)
    {
        if (change.Before is { } before)
            Set(before);
    }

    public static ChangeState Inspect(PowerSchemeChange change)
    {
        var current = Current();
        if (current == change.After)
            return ChangeState.Applied;
        return current == change.Before ? ChangeState.Original : ChangeState.Diverged;
    }

    private static Guid? Current()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    private static void Set(Guid scheme)
    {
        var result = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (result != 0)
            throw new Win32Exception((int)result, $"Güç planı etkinleştirilemedi ({scheme}).");
    }
}

internal static class DisplayModeExecutor
{
    public static DisplayModeChange Capture(DisplayModeChange desired) => desired with { Before = Current(desired.DeviceName), Captured = true };

    public static void Apply(DisplayModeChange change) => Set(change.DeviceName, change.After);

    public static void Revert(DisplayModeChange change)
    {
        if (change.Before is { } before)
            Set(change.DeviceName, before);
    }

    public static ChangeState Inspect(DisplayModeChange change)
    {
        var current = Current(change.DeviceName);
        if (current == change.After)
            return ChangeState.Applied;
        return current == change.Before ? ChangeState.Original : ChangeState.Diverged;
    }

    private static DisplayMode Current(string device)
    {
        var mode = DEVMODE.Create();
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref mode))
            throw new InvalidOperationException($"{device} için geçerli ekran modu okunamadı.");
        return new DisplayMode(mode.dmPelsWidth, mode.dmPelsHeight, mode.dmDisplayFrequency);
    }

    private static void Set(string device, DisplayMode target)
    {
        var mode = DEVMODE.Create();
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref mode))
            throw new InvalidOperationException($"{device} için geçerli ekran modu okunamadı.");

        mode.dmPelsWidth = target.Width;
        mode.dmPelsHeight = target.Height;
        mode.dmDisplayFrequency = target.RefreshHz;
        mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

        // Ask the driver first; never push a mode it says it cannot drive.
        var test = ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
        if (test != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"Sürücü bu modu desteklemiyor (kod {test}).");

        var result = ChangeDisplaySettingsEx(device, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (result != DISP_CHANGE_SUCCESSFUL)
            throw new InvalidOperationException($"Ekran modu değiştirilemedi (kod {result}).");
    }
}

internal static class MouseExecutor
{
    public static MouseAccelerationChange Capture(MouseAccelerationChange desired) => desired with { Before = Current(), Captured = true };

    public static void Apply(MouseAccelerationChange change) => Set(change.After);

    public static void Revert(MouseAccelerationChange change)
    {
        if (change.Before is { } before)
            Set(before);
    }

    public static ChangeState Inspect(MouseAccelerationChange change)
    {
        var current = Current();
        if (current == change.After)
            return ChangeState.Applied;
        return current == change.Before ? ChangeState.Original : ChangeState.Diverged;
    }

    private static MouseParameters Current()
    {
        var values = new int[3];
        if (!SystemParametersInfo(SPI_GETMOUSE, 0, values, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new MouseParameters(values[0], values[1], values[2]);
    }

    private static void Set(MouseParameters p)
    {
        // SPIF_UPDATEINIFILE persists to HKCU\Control Panel\Mouse; SENDCHANGE applies it live.
        if (!SystemParametersInfo(SPI_SETMOUSE, 0, [p.Threshold1, p.Threshold2, p.Acceleration], SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
