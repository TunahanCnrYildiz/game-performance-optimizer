using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Optimizer.Core.Changes;
using static Optimizer.Platform.Windows.Interop.ProcessNative;

namespace Optimizer.Platform.Windows.Changes;

/// <summary>
/// Efficiency mode = idle priority class + EcoQoS execution-speed throttling, the same
/// pair Task Manager's "Efficiency mode" sets. Nothing persists beyond the process.
/// </summary>
internal static class ProcessEfficiencyExecutor
{
    private static readonly int StateSize = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();

    public static ProcessEfficiencyChange Capture(ProcessEfficiencyChange desired)
    {
        using var handle = Open(desired, write: false)
                           ?? throw new InvalidOperationException($"{desired.ProcessName} (PID {desired.Pid}) artık çalışmıyor.");
        return desired with { Before = Read(handle), Captured = true };
    }

    public static void Apply(ProcessEfficiencyChange change)
    {
        using var handle = Open(change, write: true)
                           ?? throw new InvalidOperationException($"{change.ProcessName} (PID {change.Pid}) artık çalışmıyor.");
        Write(handle, new ProcessEfficiencyState(IDLE_PRIORITY_CLASS, PROCESS_POWER_THROTTLING_EXECUTION_SPEED, PROCESS_POWER_THROTTLING_EXECUTION_SPEED));
    }

    public static void Revert(ProcessEfficiencyChange change)
    {
        if (change.Before is not { } before)
            return;

        // Process gone: nothing left to restore.
        using var handle = Open(change, write: true);
        if (handle is not null)
            Write(handle, before);
    }

    public static ChangeState Inspect(ProcessEfficiencyChange change)
    {
        using var handle = Open(change, write: false);
        if (handle is null)
            return ChangeState.Original; // exited; nothing to undo

        var current = Read(handle);
        if (current.PriorityClass == IDLE_PRIORITY_CLASS && (current.ThrottleStateMask & PROCESS_POWER_THROTTLING_EXECUTION_SPEED) != 0)
            return ChangeState.Applied;
        return current == change.Before ? ChangeState.Original : ChangeState.Diverged;
    }

    /// <summary>Opens the process only if it is the same instance (PID reuse check via start time).</summary>
    private static SafeProcessHandle? Open(ProcessEfficiencyChange change, bool write)
    {
        var access = PROCESS_QUERY_LIMITED_INFORMATION | (write ? PROCESS_SET_INFORMATION : 0);
        var handle = OpenProcess(access, false, change.Pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            // 87 = invalid parameter: no such PID. Access denied is a real error worth reporting.
            if (error == 87)
                return null;
            throw new Win32Exception(error, $"{change.ProcessName} (PID {change.Pid}) açılamadı.");
        }

        if (!IsRunning(handle) || StartTimeUtc(handle) != change.StartTimeUtc)
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static ProcessEfficiencyState Read(SafeProcessHandle handle)
    {
        var priority = GetPriorityClass(handle);
        if (priority == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var state = new PROCESS_POWER_THROTTLING_STATE { Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION };
        return GetProcessInformation(handle, ProcessPowerThrottling, ref state, StateSize)
            ? new ProcessEfficiencyState(priority, state.ControlMask, state.StateMask)
            : new ProcessEfficiencyState(priority, 0, 0);
    }

    private static void Write(SafeProcessHandle handle, ProcessEfficiencyState target)
    {
        if (!SetPriorityClass(handle, target.PriorityClass))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Öncelik değiştirilemedi.");

        var state = new PROCESS_POWER_THROTTLING_STATE
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = target.ThrottleControlMask,
            StateMask = target.ThrottleStateMask,
        };
        if (!SetProcessInformation(handle, ProcessPowerThrottling, ref state, StateSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "EcoQoS ayarlanamadı.");
    }
}
