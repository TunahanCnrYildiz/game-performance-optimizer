using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Optimizer.Platform.Windows.Interop;

#pragma warning disable SYSLIB1054

internal static class ProcessNative
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_INFORMATION = 0x0200;

    public const int IDLE_PRIORITY_CLASS = 0x40;

    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessIoCounters(SafeProcessHandle process, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int GetPriorityClass(SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetPriorityClass(SafeProcessHandle process, int priorityClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessInformation(SafeProcessHandle process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessInformation(SafeProcessHandle process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    private const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    /// <summary>
    /// A process that has exited can still be opened while anyone holds a handle to it,
    /// so a successful OpenProcess does not mean it is running.
    /// </summary>
    public static bool IsRunning(SafeProcessHandle handle) =>
        GetExitCodeProcess(handle, out var code) && code == STILL_ACTIVE;

    public static string? ImagePath(SafeProcessHandle handle)
    {
        var size = 1024;
        var sb = new StringBuilder(size);
        return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
    }

    public static DateTime? StartTimeUtc(SafeProcessHandle handle) =>
        GetProcessTimes(handle, out var creation, out _, out _, out _) ? DateTime.FromFileTimeUtc(creation) : null;
}

/// <summary>Performance Data Helper: locale-independent counters (English paths) incl. wildcards.</summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;

    private IntPtr _query;

    public PdhQuery()
    {
        Check(PdhOpenQuery(null, IntPtr.Zero, out _query), "PdhOpenQuery");
    }

    public IntPtr Add(string englishPath)
    {
        Check(PdhAddEnglishCounter(_query, englishPath, IntPtr.Zero, out var counter), englishPath);
        return counter;
    }

    /// <summary>Returns false when the counter does not exist on this machine.</summary>
    public bool TryAdd(string englishPath, out IntPtr counter) =>
        PdhAddEnglishCounter(_query, englishPath, IntPtr.Zero, out counter) == 0;

    public void Collect() => PdhCollectQueryData(_query);

    public double? Value(IntPtr counter) =>
        PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var value) == 0 && value.CStatus == 0
            ? value.doubleValue
            : null;

    /// <summary>All instances of a wildcard counter, e.g. \GPU Engine(*)\Utilization Percentage.</summary>
    public List<(string Instance, double Value)> Values(IntPtr counter)
    {
        var result = new List<(string, double)>();
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0)
            return result;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, buffer) != 0)
                return result;

            // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR szName; { DWORD CStatus; double doubleValue } } = 24 bytes on x64.
            var itemSize = IntPtr.Size == 8 ? 24 : 16;
            var valueOffset = IntPtr.Size == 8 ? 16 : 8;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * itemSize;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                var cstatus = Marshal.ReadInt32(item + IntPtr.Size);
                if (name is null || cstatus != 0)
                    continue;
                result.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + valueOffset))));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    private static void Check(uint status, string what)
    {
        if (status != 0)
            throw new InvalidOperationException($"PDH hatası 0x{status:X8}: {what}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double doubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
