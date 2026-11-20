using System.Diagnostics;

namespace Optimizer.Platform.Linux;

/// <summary>
/// File access relative to a root ("/" on a real system, a fixture folder in tests), so
/// every collector can be tested against captured /proc and /sys contents.
/// </summary>
public sealed class LinuxFs(string root = "/")
{
    public static readonly LinuxFs System = new();

    public string Root { get; } = root;

    public string Path(string absolute) => System_IO_Path.Combine(Root, absolute.TrimStart('/'));

    public string? Read(string absolute)
    {
        try
        {
            var p = Path(absolute);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? ReadLine(string absolute) => Read(absolute)?.Split('\n', 2)[0].Trim();

    public long? ReadLong(string absolute) => long.TryParse(ReadLine(absolute), out var v) ? v : null;

    public bool Exists(string absolute) => File.Exists(Path(absolute)) || Directory.Exists(Path(absolute));

    public IEnumerable<string> Directories(string absolute, string pattern = "*")
    {
        var p = Path(absolute);
        if (!Directory.Exists(p))
            return [];
        try
        {
            return Directory.EnumerateDirectories(p, pattern).Select(d => "/" + System_IO_Path.GetRelativePath(Root, d).Replace('\\', '/')).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Target of a symlink (e.g. /proc/123/exe, /sys/.../driver), or null.</summary>
    public string? LinkTarget(string absolute)
    {
        try
        {
            var info = new FileInfo(Path(absolute));
            return info.LinkTarget ?? (Directory.Exists(info.FullName) ? new DirectoryInfo(info.FullName).LinkTarget : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Avoids the clash between this class's Path method and System.IO.Path.
    private static class System_IO_Path
    {
        public static string Combine(string a, string b) => global::System.IO.Path.Combine(a, b);
        public static string GetRelativePath(string relativeTo, string path) => global::System.IO.Path.GetRelativePath(relativeTo, path);
    }
}

/// <summary>Runs small helper programs (xrandr, powerprofilesctl, lspci) and returns their output.</summary>
public static class Shell
{
    /// <returns>Standard output, or null if the program is missing, fails or times out.</returns>
    public static string? Run(string file, string arguments = "", int timeoutMs = 3000)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }

            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is global::System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether a program exists on PATH.</summary>
    public static bool Exists(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin")
        .Split(':', StringSplitOptions.RemoveEmptyEntries)
        .Any(dir => File.Exists(global::System.IO.Path.Combine(dir, program)));
}
