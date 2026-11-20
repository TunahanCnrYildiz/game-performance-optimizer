using Optimizer.Core.Changes;

namespace Optimizer.Platform.Linux;

/// <summary>
/// sysctl values (runtime + this app's own drop-in file) and power-profiles-daemon profiles.
/// Writing sysctls needs root, so those changes only run inside the pkexec helper.
/// </summary>
public sealed class LinuxChangeExecutor(LinuxFs? fs = null, IPowerProfiles? profiles = null) : IChangeExecutor
{
    public const string DropInPath = "/etc/sysctl.d/99-performance-optimizer.conf";

    private readonly LinuxFs _fs = fs ?? LinuxFs.System;
    private readonly IPowerProfiles _profiles = profiles ?? new PowerProfilesCtl();

    public Change Capture(Change desired) => desired switch
    {
        SysctlChange s => s with { Before = ReadRuntime(s.Key), DropInBefore = ReadDropIn().GetValueOrDefault(s.Key), Captured = true },
        PowerProfileChange p => p with { Before = _profiles.Get(), Captured = true },
        _ => throw Unsupported(desired),
    };

    public void Apply(Change change)
    {
        switch (change)
        {
            case SysctlChange s:
                WriteRuntime(s.Key, s.After);
                SetDropIn(s.Key, s.After);
                break;
            case PowerProfileChange p:
                _profiles.Set(p.After);
                break;
            default:
                throw Unsupported(change);
        }
    }

    public void Revert(Change change)
    {
        switch (change)
        {
            case SysctlChange s:
                if (s.Before is not null)
                    WriteRuntime(s.Key, s.Before);
                SetDropIn(s.Key, s.DropInBefore);
                break;
            case PowerProfileChange { Before: { } before }:
                _profiles.Set(before);
                break;
            case PowerProfileChange:
                break;
            default:
                throw Unsupported(change);
        }
    }

    public ChangeState Inspect(Change change)
    {
        switch (change)
        {
            case SysctlChange s:
            {
                var runtime = ReadRuntime(s.Key);
                var persisted = ReadDropIn().GetValueOrDefault(s.Key);
                if (runtime == s.After && persisted == s.After)
                    return ChangeState.Applied;
                return runtime == s.Before && persisted == s.DropInBefore ? ChangeState.Original : ChangeState.Diverged;
            }
            case PowerProfileChange p:
            {
                var current = _profiles.Get();
                if (current == p.After)
                    return ChangeState.Applied;
                return current == p.Before ? ChangeState.Original : ChangeState.Diverged;
            }
            default:
                throw Unsupported(change);
        }
    }

    /// <summary>Copy of the drop-in before the first change, next to the journal entry.</summary>
    public void Backup(Change change, string backupDirectory)
    {
        if (change is not SysctlChange || _fs.Read(DropInPath) is not { } text)
            return;
        Directory.CreateDirectory(backupDirectory);
        var target = Path.Combine(backupDirectory, "99-performance-optimizer.conf");
        if (!File.Exists(target))
            File.WriteAllText(target, text);
    }

    private static string ProcPath(string key) => "/proc/sys/" + key.Replace('.', '/');

    private string? ReadRuntime(string key) => _fs.ReadLine(ProcPath(key));

    private void WriteRuntime(string key, string value) => File.WriteAllText(_fs.Path(ProcPath(key)), value + "\n");

    private Dictionary<string, string> ReadDropIn() =>
        (_fs.Read(DropInPath) ?? "")
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split('=', 2))
        .Where(p => p.Length == 2)
        .GroupBy(p => p[0].Trim())
        .ToDictionary(g => g.Key, g => g.Last()[1].Trim());

    /// <summary>Sets or removes one key in our drop-in; deletes the file when nothing of ours is left.</summary>
    private void SetDropIn(string key, string? value)
    {
        var entries = ReadDropIn();
        if (value is null)
            entries.Remove(key);
        else
            entries[key] = value;

        var path = _fs.Path(DropInPath);
        if (entries.Count == 0)
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string>
        {
            "# Written by Performance Optimizer. Remove this file (or use the app's history) to undo.",
        };
        lines.AddRange(entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} = {e.Value}"));
        var temp = path + ".tmp";
        File.WriteAllText(temp, string.Join('\n', lines) + "\n");
        File.Move(temp, path, overwrite: true);
    }

    private static NotSupportedException Unsupported(Change change) => new($"Linux'ta desteklenmeyen değişiklik türü: {change.GetType().Name}");
}

public interface IPowerProfiles
{
    string? Get();
    IReadOnlyList<string> Available();
    void Set(string profile);
}

/// <summary>power-profiles-daemon via its CLI; the active desktop user may switch profiles without root.</summary>
public sealed class PowerProfilesCtl : IPowerProfiles
{
    public string? Get() => Shell.Run("powerprofilesctl", "get")?.Trim() is { Length: > 0 } p ? p : null;

    public IReadOnlyList<string> Available() =>
        (Shell.Run("powerprofilesctl", "list") ?? "")
        .Split('\n')
        .Select(l => l.Trim().TrimStart('*').Trim())
        .Where(l => l.EndsWith(':'))
        .Select(l => l.TrimEnd(':'))
        .ToList();

    public void Set(string profile)
    {
        if (Shell.Run("powerprofilesctl", $"set {profile}") is null)
            throw new InvalidOperationException($"Güç profili '{profile}' olarak ayarlanamadı (powerprofilesctl).");
    }
}
