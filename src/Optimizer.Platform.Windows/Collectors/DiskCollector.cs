using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Optimizer.Core.Inventory;

namespace Optimizer.Platform.Windows.Collectors;

internal static partial class DiskCollector
{
    public static IReadOnlyList<DiskInfo> Collect()
    {
        var media = WmiCollectors.CollectDriveMedia();
        var libraries = FindGameLibraries();
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        var result = new List<DiskInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                continue;

            var letter = char.ToUpperInvariant(drive.Name[0]);
            media.TryGetValue(letter, out var info);

            result.Add(new DiskInfo
            {
                RootPath = drive.RootDirectory.FullName,
                Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.AvailableFreeSpace,
                IsSystemDrive = string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase),
                MediaType = info.Media,
                BusType = info.Bus,
                GameLibraries = libraries
                    .Where(l => string.Equals(Path.GetPathRoot(l), drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                    .ToList(),
            });
        }

        return result;
    }

    private static IReadOnlyList<string> FindGameLibraries()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAdd(found, FindSteamLibraries);
        TryAdd(found, FindEpicLibraries);
        return found.ToList();
    }

    private static void TryAdd(HashSet<string> target, Func<IEnumerable<string>> source)
    {
        try
        {
            foreach (var path in source())
            {
                if (Directory.Exists(path))
                    target.Add(Path.GetFullPath(path).TrimEnd('\\'));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A launcher with a broken config should not break the scan.
        }
    }

    private static IEnumerable<string> FindSteamLibraries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steamPath)
            yield break;

        var vdf = Path.Combine(steamPath.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;

        foreach (Match m in SteamPathRegex().Matches(File.ReadAllText(vdf)))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    private static IEnumerable<string> FindEpicLibraries()
    {
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
            yield break;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } install)
            {
                // Report the folder that holds the games, not each game.
                var parent = Path.GetDirectoryName(install.TrimEnd('\\', '/'));
                if (parent is not null)
                    yield return parent;
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathRegex();
}
