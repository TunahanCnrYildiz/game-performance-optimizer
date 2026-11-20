namespace Optimizer.Core.Changes;

public static class BootClock
{
    /// <summary>When the OS last started, derived from uptime. Stable within a boot to well under a second.</summary>
    public static DateTimeOffset Current() =>
        DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
