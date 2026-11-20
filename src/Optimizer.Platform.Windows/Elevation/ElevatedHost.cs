using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimizer.Core.Changes;
using Optimizer.Platform.Windows.Changes;
using Optimizer.Platform.Windows.Sessions;

namespace Optimizer.Platform.Windows.Elevation;

public enum ElevatedOperation { Apply, Revert }

public sealed record ElevatedRequest
{
    public required ElevatedOperation Operation { get; init; }
    public required string JournalDirectory { get; init; }

    // Apply
    public string? TweakId { get; init; }
    public string? Title { get; init; }
    public IReadOnlyList<Change> Changes { get; init; } = [];
    public bool KeepPending { get; init; }
    public bool RequiresRestart { get; init; }
    public bool CreateRestorePoint { get; init; }
    public bool RequireRestorePoint { get; init; }

    // Revert
    public Guid? EntryId { get; init; }
}

public sealed record ElevatedResponse
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public Guid? EntryId { get; init; }
    public bool NothingToDo { get; init; }

    /// <summary>Null when not requested or created; otherwise why the restore point failed.</summary>
    public string? RestorePointError { get; init; }

    public bool RestorePointCreated { get; init; }
}

/// <summary>
/// Runs inside the short-lived elevated copy of the app. The request file is written by
/// the unelevated app, so it is untrusted: every change is checked against
/// <see cref="ElevatedChangePolicy"/> before anything is touched — for apply and for revert
/// (a tampered journal must not become a way to write arbitrary system settings).
/// </summary>
public static class ElevatedHost
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Entry point: <c>--elevated &lt;request.json&gt; &lt;response.json&gt;</c>. Returns the process exit code.</summary>
    public static int Run(string requestPath, string responsePath)
    {
        ElevatedResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<ElevatedRequest>(File.ReadAllText(requestPath), Json)
                          ?? throw new InvalidDataException("İstek okunamadı.");
            response = Execute(request, new WindowsChangeExecutor(), ElevatedChangePolicy.ForThisMachine(), RestorePoint.TryCreate);
        }
        catch (Exception ex)
        {
            response = new ElevatedResponse { Success = false, Error = ex.Message };
        }

        File.WriteAllText(responsePath, JsonSerializer.Serialize(response, Json));
        return response.Success ? 0 : 1;
    }

    internal static ElevatedResponse Execute(
        ElevatedRequest request,
        IChangeExecutor executor,
        ElevatedChangePolicy policy,
        Func<string, string?> createRestorePoint)
    {
        var engine = new TweakEngine(executor, new Journal(request.JournalDirectory), "elevated");

        if (request.Operation == ElevatedOperation.Revert)
        {
            var entry = request.EntryId is { } id ? engine.Journal.Get(id) : null;
            if (entry is null)
                return new ElevatedResponse { Success = false, Error = "Geri alınacak kayıt bulunamadı." };
            if (entry.Changes.FirstOrDefault(c => !policy.IsAllowed(c)) is { } bad)
                return new ElevatedResponse { Success = false, Error = $"Güvenlik nedeniyle reddedildi: {bad.Describe()}" };

            var reverted = engine.Revert(entry.Id);
            return new ElevatedResponse { Success = reverted.Success, Error = reverted.Error, EntryId = entry.Id };
        }

        if (request.Changes.Count == 0 || request.TweakId is null || request.Title is null)
            return new ElevatedResponse { Success = false, Error = "Geçersiz istek." };
        if (request.Changes.FirstOrDefault(c => !policy.IsAllowed(c)) is { } rejected)
            return new ElevatedResponse { Success = false, Error = $"Güvenlik nedeniyle reddedildi: {rejected.Describe()}" };

        string? restoreError = null;
        var restoreCreated = false;
        if (request.CreateRestorePoint)
        {
            restoreError = createRestorePoint($"Performance Optimizer: {request.Title}");
            restoreCreated = restoreError is null;
            if (!restoreCreated && request.RequireRestorePoint)
                return new ElevatedResponse { Success = false, Error = restoreError, RestorePointError = restoreError };
        }

        var tweak = new ManualTweak(request.TweakId, request.Title, Risk.Medium, RequiresAdmin: true, RequiresRestart: request.RequiresRestart);
        var result = engine.Apply(tweak, request.Changes, keepPending: request.KeepPending || request.RequiresRestart);
        return new ElevatedResponse
        {
            Success = result.Success,
            Error = result.Error,
            EntryId = result.Entry?.Id,
            NothingToDo = result.NothingToDo,
            RestorePointError = restoreError,
            RestorePointCreated = restoreCreated,
        };
    }
}

/// <summary>The only things the elevated helper will ever change.</summary>
public sealed class ElevatedChangePolicy(IReadOnlyCollection<string> allowedExclusionFolders)
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

/// <summary>Unelevated side: runs one request through a UAC-elevated copy of the current executable.</summary>
public sealed class ElevatedRunner(string executablePath)
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
