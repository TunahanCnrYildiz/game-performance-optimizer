using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Optimizer.Platform.Windows.Sessions;

/// <summary>Folders that contain installed games: Steam's "steamapps\common" in every library, Epic install parents.</summary>
public static partial class GameLibraryLocator
{
    public static IReadOnlyList<string> FindGameRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAdd(roots, SteamRoots);
        TryAdd(roots, EpicRoots);
        return roots.ToList();
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
        }
    }

    private static IEnumerable<string> SteamRoots()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string steamPath)
            yield break;

        steamPath = steamPath.Replace('/', '\\');
        yield return Path.Combine(steamPath, "steamapps", "common");

        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf))
            yield break;

        foreach (Match m in SteamPathRegex().Matches(File.ReadAllText(vdf)))
            yield return Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common");
    }

    private static IEnumerable<string> EpicRoots()
    {
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
            yield break;

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } install)
            {
                if (Path.GetDirectoryName(install.TrimEnd('\\', '/')) is { } parent)
                    yield return parent;
            }
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPathRegex();
}
