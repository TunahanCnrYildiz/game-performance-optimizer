using System.Diagnostics;
using System.Text.Json;
using Optimizer.Core.Changes;
using Optimizer.Core.Elevation;
using Optimizer.Platform.Windows.Changes;
using Optimizer.Platform.Windows.Sessions;

namespace Optimizer.Platform.Windows.Elevation;

/// <summary>The only things the elevated helper will ever change.</summary>
public sealed class ElevatedChangePolicy(IReadOnlyCollection<string> allowedExclusionFolders) : IElevationPolicy
{
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string Hvci = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";
    private const string StartupApproved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static ElevatedChangePolicy ForThisMachine() => new(GameLibraryLocator.FindGameRoots());

    public bool IsAllowed(Change change) => change switch
    {
        RegistryValueChange r => IsAllowedRegistry(r),
        DefenderExclusionChange d => allowedExclusionFolders.Any(f => string.Equals(
            f.TrimEnd('\\'), d.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static bool IsAllowedRegistry(RegistryValueChange r)
    {
        if (r.Hive != "HKLM")
            return false; // per-user settings never need elevation

        bool Is(string key, string name) =>
            string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase);

        if (Is(GraphicsDrivers, "HwSchMode"))
            return DwordIn(r.After, 1, 2) && DwordIn(r.Before, 1, 2, null);

        if (Is(Hvci, "Enabled"))
            return DwordIn(r.After, 0, 1) && DwordIn(r.Before, 0, 1, null);

        // Task Manager's enable/disable blob for machine-wide startup entries.
        if (r.Key.StartsWith(StartupApproved, StringComparison.OrdinalIgnoreCase)
            && r.Key[StartupApproved.Length..] is "Run" or "Run32" or "StartupFolder")
            return IsStartupBlob(r.After) && (r.Before is null || IsStartupBlob(r.Before));

        return false;
    }

    private static bool DwordIn(RegistryData? data, params int?[] allowed) =>
        data is null
            ? allowed.Contains(null)
            : data.Kind == RegistryDataKind.Dword && int.TryParse(data.Value, out var v) && allowed.Contains(v);

    private static bool IsStartupBlob(RegistryData? data) =>
        data is { Kind: RegistryDataKind.Binary, Value.Length: 24 } && data.Value[..2] is "02" or "03" or "06" or "07";
}

/// <summary>Unelevated side (Windows): runs one request through a UAC-elevated copy of the current executable.</summary>
public sealed class ElevatedRunner(string executablePath) : IElevatedRunner
{
    public static ElevatedRunner ForCurrentProcess() =>
        new(Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı."));

    public async Task<ElevatedResponse> RunAsync(ElevatedRequest request, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PerformanceOptimizer", "elevated");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(dir, $"{id}-request.json");
        var responsePath = Path.Combine(dir, $"{id}-response.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request, ElevatedHost.Json), cancellationToken);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--elevated \"{requestPath}\" \"{responsePath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException("Yönetici işlemi başlatılamadı.");

            await process.WaitForExitAsync(cancellationToken);

            return File.Exists(responsePath)
                ? JsonSerializer.Deserialize<ElevatedResponse>(await File.ReadAllTextAsync(responsePath, cancellationToken), ElevatedHost.Json)
                  ?? new ElevatedResponse { Success = false, Error = "Yanıt okunamadı." }
                : new ElevatedResponse { Success = false, Error = "Yönetici işlemi yanıt vermedi." };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new ElevatedResponse { Success = false, Error = "Yönetici izni verilmedi; hiçbir şey değiştirilmedi." };
        }
        finally
        {
            foreach (var f in new[] { requestPath, responsePath })
            {
                try { File.Delete(f); } catch (IOException) { }
            }
        }
    }
}

public static class WindowsElevatedHelper
{
    /// <summary>Entry point of the elevated copy: <c>--elevated &lt;request.json&gt; &lt;response.json&gt;</c>.</summary>
    public static int Run(string requestPath, string responsePath) =>
        ElevatedHost.RunFromFiles(requestPath, responsePath, new WindowsChangeExecutor(), ElevatedChangePolicy.ForThisMachine(), RestorePoint.TryCreate);
}
