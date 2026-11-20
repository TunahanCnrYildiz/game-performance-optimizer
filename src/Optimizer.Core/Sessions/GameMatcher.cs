using System.Text.RegularExpressions;

namespace Optimizer.Core.Sessions;

/// <summary>
/// Decides which running process is a game: anything started from a game library
/// folder (Steam "steamapps\common", Epic install folder) or an executable the user
/// added. Helpers that live next to games (crash reporters, anti-cheat, redistributables)
/// are ignored.
/// </summary>
public sealed partial class GameMatcher
{
    private readonly IReadOnlyList<string> _roots;
    private readonly HashSet<string> _userGames;
    private readonly HashSet<string> _ignored;

    public GameMatcher(IEnumerable<string> libraryRoots, IEnumerable<string> userGames, IEnumerable<string> ignoredGames)
    {
        _roots = libraryRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.TrimEnd('\\', '/') + "\\")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _userGames = new HashSet<string>(userGames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        _ignored = new HashSet<string>(ignoredGames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsGame(ProcessSample process)
    {
        if (process.Path is not { Length: > 0 } path)
            return false;

        var file = System.IO.Path.GetFileName(path);
        if (_ignored.Contains(Normalize(path)) || _ignored.Contains(Normalize(file)))
            return false;

        if (_userGames.Contains(Normalize(path)) || _userGames.Contains(Normalize(file)))
            return true;

        if (HelperRegex().IsMatch(file))
            return false;

        return _roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The game among running processes; when several match, the one using the most memory.</summary>
    public ProcessSample? FindGame(SystemSample sample) =>
        sample.Processes.Where(IsGame).MaxBy(p => p.WorkingSetBytes);

    private static string Normalize(string value) => value.Trim().Replace('/', '\\');

    [GeneratedRegex(
        @"crash|report|redist|vc_?redist|directx|dxsetup|unins|setup|installer|easyanticheat|beservice|battleye|start_protected_game|cefprocess|webhelper|helper\.exe$",
        RegexOptions.IgnoreCase)]
    private static partial Regex HelperRegex();
}
