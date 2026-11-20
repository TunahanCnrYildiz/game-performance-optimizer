using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimizer.Core.Changes;

public enum JournalStatus
{
    /// <summary>Written before applying, or applied but waiting for the user to confirm. Reverted automatically on next start.</summary>
    Pending,

    Applied,

    /// <summary>Applying failed part-way and everything already applied was undone.</summary>
    RolledBack,

    Reverted,

    /// <summary>Reverted, but some values were left alone because something else changed them in between.</summary>
    PartiallyReverted,
}

public sealed record JournalEntry
{
    public required Guid Id { get; init; }
    public required string TweakId { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string AppVersion { get; init; }
    public required JournalStatus Status { get; init; }
    public required IReadOnlyList<Change> Changes { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool CanRevert => Status is JournalStatus.Applied or JournalStatus.Pending;
}

/// <summary>
/// Durable, human-readable record of every change, one JSON file per entry. Writes
/// are atomic (temp file + rename) so a crash never leaves a half-written entry.
/// </summary>
public sealed class Journal
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _directory;

    public Journal(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    public string BackupDirectoryFor(Guid entryId) => Path.Combine(_directory, "backups", entryId.ToString("N"));

    public void Save(JournalEntry entry)
    {
        var path = PathFor(entry.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entry, Options));
        File.Move(temp, path, overwrite: true);
    }

    public JournalEntry? Get(Guid id)
    {
        var path = PathFor(id);
        return File.Exists(path) ? Read(path) : null;
    }

    /// <summary>All readable entries, newest first. Corrupt files are skipped, never deleted.</summary>
    public IReadOnlyList<JournalEntry> LoadAll() =>
        Directory.EnumerateFiles(_directory, "*.json")
            .Select(TryRead)
            .OfType<JournalEntry>()
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

    private string PathFor(Guid id) => Path.Combine(_directory, $"{id:N}.json");

    private static JournalEntry? Read(string path) => JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(path), Options);

    private static JournalEntry? TryRead(string path)
    {
        try
        {
            return Read(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
