namespace Optimizer.Platform.Windows;

public static class WindowsAppPaths
{
    /// <summary>
    /// Shared by the desktop app and the CLI so that either can undo what the other did.
    /// Per-user, because every phase 2 change is a per-user setting.
    /// </summary>
    public static string JournalDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerformanceOptimizer", "journal");
}
