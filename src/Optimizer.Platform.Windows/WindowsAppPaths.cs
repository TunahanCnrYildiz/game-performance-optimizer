namespace Optimizer.Platform.Windows;

/// <summary>
/// Shared by the desktop app and the CLI so that either can undo what the other did.
/// Per-user, because every change so far is a per-user setting or a user's own process.
/// </summary>
public static class WindowsAppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PerformanceOptimizer");

    public static string JournalDirectory { get; } = Path.Combine(Root, "journal");

    public static string SessionsDirectory { get; } = Path.Combine(Root, "sessions");

    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    public static string BenchmarksDirectory { get; } = Path.Combine(Root, "benchmarks");
}
