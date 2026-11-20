using Microsoft.Win32;
using Optimizer.Core.Inventory;

namespace Optimizer.Platform.Windows.Collectors;

internal static class RegistryCollectors
{
    private const string UserGpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GlobalSettingsValue = "DirectXUserGlobalSettings";

    public static GamingSettings CollectGaming()
    {
        using var gameBar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
        using var gameDvr = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
        using var graphics = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
        using var mouse = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
        using var gpuPrefs = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesKey);

        // Game Mode is on unless explicitly disabled.
        var gameMode = gameBar?.GetValue("AutoGameModeEnabled") is int gm && gm == 0 ? Toggle.Off : Toggle.On;

        var capture = gameDvr?.GetValue("AppCaptureEnabled") is int ac && ac == 0 ? Toggle.Off : Toggle.On;
        var historical = gameDvr?.GetValue("HistoricalCaptureEnabled") is int hc && hc == 1 ? Toggle.On : Toggle.Off;
        if (capture == Toggle.Off)
            historical = Toggle.Off;

        var hags = graphics?.GetValue("HwSchMode") switch
        {
            2 => Toggle.On,
            1 => Toggle.Off,
            _ => Toggle.Unknown,
        };

        var pointer = (mouse?.GetValue("MouseSpeed") as string) switch
        {
            "0" => Toggle.Off,
            null => Toggle.Unknown,
            _ => Toggle.On,
        };

        var global = ParseSemicolonPairs(gpuPrefs?.GetValue(GlobalSettingsValue) as string);
        var preferences = new List<GpuPreferenceEntry>();
        if (gpuPrefs is not null)
        {
            foreach (var name in gpuPrefs.GetValueNames())
            {
                if (name.Equals(GlobalSettingsValue, StringComparison.OrdinalIgnoreCase))
                    continue;

                var pairs = ParseSemicolonPairs(gpuPrefs.GetValue(name) as string);
                if (pairs.TryGetValue("GpuPreference", out var pref) && int.TryParse(pref, out var p))
                    preferences.Add(new GpuPreferenceEntry(name, p));
            }
        }

        return new GamingSettings
        {
            GameMode = gameMode,
            GameDvrCapture = capture,
            BackgroundRecording = historical,
            Hags = hags,
            WindowedGameOptimizations = PairToggle(global, "SwapEffectUpgradeEnable"),
            VrrOptimizations = PairToggle(global, "VRROptimizeEnable"),
            EnhancePointerPrecision = pointer,
            GpuPreferences = preferences,
        };
    }

    public static IReadOnlyList<StartupItem> CollectStartupItems()
    {
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        var items = new List<StartupItem>();
        ReadRunKey(items, Registry.CurrentUser, run, approved + "Run", "Kullanıcı (Run)");
        ReadRunKey(items, Registry.LocalMachine, run, approved + "Run", "Tüm kullanıcılar (Run)");
        ReadRunKey(items, Registry.LocalMachine, run32, approved + "Run32", "Tüm kullanıcılar (Run, 32 bit)");
        ReadStartupFolder(items, Registry.CurrentUser, Environment.GetFolderPath(Environment.SpecialFolder.Startup), approved + "StartupFolder", "Kullanıcı başlangıç klasörü");
        ReadStartupFolder(items, Registry.LocalMachine, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), approved + "StartupFolder", "Ortak başlangıç klasörü");
        return items;
    }

    private static void ReadRunKey(List<StartupItem> items, RegistryKey hive, string runPath, string approvedPath, string location)
    {
        using var key = hive.OpenSubKey(runPath);
        if (key is null)
            return;

        using var approvedKey = hive.OpenSubKey(approvedPath);
        foreach (var name in key.GetValueNames().Where(n => !string.IsNullOrEmpty(n)))
        {
            items.Add(new StartupItem(name, key.GetValue(name)?.ToString() ?? "", location, IsApproved(approvedKey, name)));
        }
    }

    private static void ReadStartupFolder(List<StartupItem> items, RegistryKey hive, string folder, string approvedPath, string location)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        using var approvedKey = hive.OpenSubKey(approvedPath);
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(file);
            items.Add(new StartupItem(Path.GetFileNameWithoutExtension(file), file, location, IsApproved(approvedKey, fileName)));
        }
    }

    /// <summary>
    /// Task Manager stores enable/disable state as a 12-byte blob; an odd first byte means disabled.
    /// A missing entry means enabled.
    /// </summary>
    private static bool IsApproved(RegistryKey? approvedKey, string name) =>
        approvedKey?.GetValue(name) is not byte[] { Length: > 0 } blob || (blob[0] & 1) == 0;

    private static Toggle PairToggle(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var v) ? (v == "1" ? Toggle.On : Toggle.Off) : Toggle.Unknown;

    internal static Dictionary<string, string> ParseSemicolonPairs(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
                result[part[..eq]] = part[(eq + 1)..];
        }

        return result;
    }
}
