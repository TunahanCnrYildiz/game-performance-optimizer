using System.Text.Json;
using System.Text.Json.Serialization;
using Optimizer.Core.Changes;

namespace Optimizer.Core.Elevation;

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

/// <summary>Unelevated side: runs one request through an elevated copy of the app (UAC, pkexec, ...).</summary>
public interface IElevatedRunner
{
    Task<ElevatedResponse> RunAsync(ElevatedRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The only things a platform's elevated helper will ever change.</summary>
public interface IElevationPolicy
{
    bool IsAllowed(Change change);
}

/// <summary>
/// Runs inside the short-lived elevated helper. The request file is written by the
/// unelevated app, so it is untrusted: every change is checked against the policy before
/// anything is touched — for apply and for revert (a tampered journal must not become a
/// way to write arbitrary system settings).
/// </summary>
public static class ElevatedHost
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool IsElevated() => Environment.IsPrivilegedProcess;

    public static int RunFromFiles(
        string requestPath,
        string responsePath,
        IChangeExecutor executor,
        IElevationPolicy policy,
        Func<string, string?> createRestorePoint)
    {
        ElevatedResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<ElevatedRequest>(File.ReadAllText(requestPath), Json)
                          ?? throw new InvalidDataException("İstek okunamadı.");
            response = Execute(request, executor, policy, createRestorePoint);
        }
        catch (Exception ex)
        {
            response = new ElevatedResponse { Success = false, Error = ex.Message };
        }

        File.WriteAllText(responsePath, JsonSerializer.Serialize(response, Json));
        return response.Success ? 0 : 1;
    }

    public static ElevatedResponse Execute(
        ElevatedRequest request,
        IChangeExecutor executor,
        IElevationPolicy policy,
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
