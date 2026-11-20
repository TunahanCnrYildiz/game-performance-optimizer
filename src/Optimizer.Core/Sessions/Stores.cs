using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimizer.Core.Sessions;

public sealed record AppSettings
{
    /// <summary>Watch for games while the app is running.</summary>
    public bool MonitorSessions { get; init; } = true;

    /// <summary>Process names (without .exe) put into efficiency mode while a game runs.</summary>
    public IReadOnlyList<string> EfficiencyModeApps { get; init; } = [];

    /// <summary>Executables (full path or file name) treated as games even outside known libraries.</summary>
    public IReadOnlyList<string> UserGames { get; init; } = [];

    /// <summary>Executables never treated as games.</summary>
    public IReadOnlyList<string> IgnoredGames { get; init; } = [];
}

internal static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, overwrite: true);
    }

    public static T? TryRead<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}

public sealed class SettingsStore(string path)
{
    private readonly Lock _lock = new();
    private AppSettings? _cached;

    public AppSettings Load()
    {
        lock (_lock)
            return _cached ??= JsonFiles.TryRead<AppSettings>(path) ?? new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            JsonFiles.WriteAtomic(path, settings);
            _cached = settings;
        }
    }

    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        lock (_lock)
        {
            var updated = change(Load());
            Save(updated);
            return updated;
        }
    }
}

public sealed class SessionStore(string directory)
{
    public void Save(SessionReport report) =>
        JsonFiles.WriteAtomic(Path.Combine(directory, $"{report.StartedAt:yyyyMMdd-HHmmss}-{report.Id:N}.json"), report);

    public IReadOnlyList<SessionReport> LoadAll() =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json")
                .Select(JsonFiles.TryRead<SessionReport>)
                .OfType<SessionReport>()
                .OrderByDescending(r => r.StartedAt)
                .ToList()
            : [];
}
