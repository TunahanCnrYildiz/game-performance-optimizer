using System.Diagnostics;
using System.Text.Json;
using Optimizer.Core.Changes;
using Optimizer.Core.Elevation;

namespace Optimizer.Platform.Linux;

/// <summary>The only things the pkexec helper will ever change.</summary>
public sealed class LinuxElevationPolicy : IElevationPolicy
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["vm.max_map_count"] = ["1048576", "2147483642"],
        ["kernel.split_lock_mitigate"] = ["0", "1"],
    };

    public bool IsAllowed(Change change) => change is SysctlChange s
        && Allowed.TryGetValue(s.Key, out var values)
        && values.Contains(s.After)
        // Original values come from the system itself; they must at least be plain numbers.
        && (s.Before is null || s.Before.All(char.IsDigit))
        && (s.DropInBefore is null || s.DropInBefore.All(char.IsDigit));
}

/// <summary>Unelevated side: runs one request through `pkexec &lt;this app&gt; --elevated`.</summary>
public sealed class PkexecRunner(string workDirectory) : IElevatedRunner
{
    public async Task<ElevatedResponse> RunAsync(ElevatedRequest request, CancellationToken cancellationToken = default)
    {
        if (!Shell.Exists("pkexec"))
            return new ElevatedResponse { Success = false, Error = "pkexec bulunamadı; yönetici işlemleri için polkit gerekli." };

        // A folder the user owns: root's files in /tmp could not be deleted by the user afterwards (sticky bit).
        Directory.CreateDirectory(workDirectory);
        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(workDirectory, $"{id}-request.json");
        var responsePath = Path.Combine(workDirectory, $"{id}-response.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, ElevatedHost.Json), cancellationToken);

        try
        {
            var start = new ProcessStartInfo("pkexec") { UseShellExecute = false };
            foreach (var arg in SelfCommand())
                start.ArgumentList.Add(arg);
            start.ArgumentList.Add("--elevated");
            start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add(responsePath);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("pkexec başlatılamadı.");
            await process.WaitForExitAsync(cancellationToken);

            // 126: the user dismissed the dialog; 127: not authorized.
            if (process.ExitCode is 126 or 127)
                return new ElevatedResponse { Success = false, Error = "Yönetici izni verilmedi; hiçbir şey değiştirilmedi." };

            return File.Exists(responsePath)
                ? JsonSerializer.Deserialize<ElevatedResponse>(await File.ReadAllTextAsync(responsePath, cancellationToken), ElevatedHost.Json)
                  ?? new ElevatedResponse { Success = false, Error = "Yanıt okunamadı." }
                : new ElevatedResponse { Success = false, Error = "Yönetici işlemi yanıt vermedi." };
        }
        finally
        {
            foreach (var f in new[] { requestPath, responsePath })
            {
                try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>The apphost, or `dotnet app.dll` when started through the dotnet host.</summary>
    private static IEnumerable<string> SelfCommand()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı.");
        if (Path.GetFileNameWithoutExtension(process) == "dotnet")
            return [process, Environment.GetCommandLineArgs()[0]];
        return [process];
    }
}

public static class LinuxElevatedHelper
{
    /// <summary>Runs as root under pkexec; hands created journal files back to the user afterwards.</summary>
    public static int Run(string requestPath, string responsePath)
    {
        var exit = ElevatedHost.RunFromFiles(requestPath, responsePath, new LinuxChangeExecutor(), new LinuxElevationPolicy(), _ => "Linux'ta Sistem Geri Yükleme yok.");

        try
        {
            var request = JsonSerializer.Deserialize<ElevatedRequest>(File.ReadAllText(requestPath), ElevatedHost.Json);
            if (request is not null)
            {
                // Same owner as the folder (the user), so the unelevated app can keep updating its journal.
                Shell.Run("chown", $"-R --reference=\"{request.JournalDirectory}\" \"{request.JournalDirectory}\"");
                Shell.Run("chown", $"--reference=\"{Path.GetDirectoryName(responsePath)}\" \"{responsePath}\"");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }

        return exit;
    }
}
