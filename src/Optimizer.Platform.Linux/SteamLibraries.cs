using System.Text.RegularExpressions;

namespace Optimizer.Platform.Linux;

/// <summary>Game folders on Linux: every Steam library's steamapps/common (native, Flatpak) and Heroic's default folder.</summary>
public static partial class SteamLibraries
{
    public static IReadOnlyList<string> FindGameRoots(LinuxFs fs, string? home)
    {
        if (string.IsNullOrEmpty(home))
            return [];

        var steamDirs = new[]
        {
            $"{home}/.local/share/Steam",
            $"{home}/.steam/steam",
            $"{home}/.var/app/com.valvesoftware.Steam/.local/share/Steam",
        };

        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var steam in steamDirs.Where(fs.Exists))
        {
            AddIfExists(fs, roots, $"{steam}/steamapps/common");
            if (fs.Read($"{steam}/steamapps/libraryfolders.vdf") is { } vdf)
            {
                foreach (Match m in PathRegex().Matches(vdf))
                    AddIfExists(fs, roots, $"{m.Groups[1].Value.Replace(@"\\", "/").TrimEnd('/')}/steamapps/common");
            }
        }

        AddIfExists(fs, roots, $"{home}/Games/Heroic");
        return roots.ToList();
    }

    private static void AddIfExists(LinuxFs fs, HashSet<string> roots, string path)
    {
        if (fs.Exists(path))
        {
            // ~/.steam/steam is usually a symlink to ~/.local/share/Steam; keep one spelling of each folder.
            var resolved = fs.Root == "/" ? ResolveSymlinks(path) : path;
            roots.Add(resolved.TrimEnd('/'));
        }
    }

    private static string ResolveSymlinks(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return path;
        }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex PathRegex();
}
