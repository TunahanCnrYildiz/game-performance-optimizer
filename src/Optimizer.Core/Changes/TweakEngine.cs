namespace Optimizer.Core.Changes;

public sealed record ApplyResult(bool Success, JournalEntry? Entry, string? Error = null, bool NothingToDo = false);

public sealed record RevertResult(bool Success, JournalEntry? Entry, string? Error = null);

/// <summary>
/// Applies tweaks transactionally:
/// capture originals → journal (Pending) → backup → apply one by one → verify → journal (Applied).
/// Any failure undoes what was already applied, newest first.
/// </summary>
public sealed class TweakEngine(
    IChangeExecutor executor,
    Journal journal,
    string appVersion,
    TimeProvider? clock = null,
    Func<DateTimeOffset>? bootTime = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<DateTimeOffset> _bootTime = bootTime ?? BootClock.Current;

    public Journal Journal => journal;

    /// <param name="keepPending">
    /// Leave the entry Pending after a successful apply; the caller must <see cref="Confirm"/> it or
    /// <see cref="Revert"/> it. If the app dies meanwhile, <see cref="RecoverPending"/> reverts it on next start.
    /// </param>
    public ApplyResult Apply(ITweak tweak, IReadOnlyList<Change> desired, bool keepPending = false)
    {
        if (desired.Count == 0)
            return new ApplyResult(true, null, NothingToDo: true);

        List<Change> captured;
        try
        {
            captured = desired.Select(executor.Capture).ToList();
        }
        catch (Exception ex)
        {
            return new ApplyResult(false, null, $"Mevcut durum okunamadı: {ex.Message}");
        }

        if (captured.Any(c => !c.Captured))
            return new ApplyResult(false, null, "Yürütücü özgün durumu kaydetmedi; güvenlik için işlem yapılmadı.");

        // Skip values already at the target so reverting never "restores" something we did not change.
        var toApply = captured.Where(c => executor.Inspect(c) != ChangeState.Applied).ToList();
        if (toApply.Count == 0)
            return new ApplyResult(true, null, NothingToDo: true);

        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            TweakId = tweak.Id,
            Title = tweak.Title,
            CreatedAt = _clock.GetLocalNow(),
            AppVersion = appVersion,
            Status = JournalStatus.Pending,
            Changes = toApply,
            RequiresAdmin = tweak.RequiresAdmin,
            RestartRequired = tweak.RequiresRestart,
            BootTime = _bootTime(),
        };

        // The journal must exist on disk before the first byte of the system is touched.
        journal.Save(entry);

        var notes = new List<string>();
        var backupDir = journal.BackupDirectoryFor(entry.Id);
        foreach (var change in toApply)
        {
            try
            {
                executor.Backup(change, backupDir);
            }
            catch (Exception ex)
            {
                notes.Add($"Yedek alınamadı ({change.Describe()}): {ex.Message}");
            }
        }

        var applied = new List<Change>();
        try
        {
            foreach (var change in toApply)
            {
                executor.Apply(change);
                applied.Add(change);
            }

            var notApplied = toApply.Where(c => executor.Inspect(c) != ChangeState.Applied).ToList();
            if (notApplied.Count > 0)
                throw new InvalidOperationException("Doğrulama başarısız: " + string.Join("; ", notApplied.Select(c => c.Describe())));
        }
        catch (Exception ex)
        {
            var rollbackErrors = RevertAll(applied);
            notes.Add($"Uygulama hatası: {ex.Message}");
            notes.AddRange(rollbackErrors);

            var rolledBack = entry with
            {
                Status = JournalStatus.RolledBack,
                ClosedAt = _clock.GetLocalNow(),
                Notes = notes,
            };
            journal.Save(rolledBack);

            var message = rollbackErrors.Count == 0
                ? $"{ex.Message} Yapılan değişiklikler geri alındı."
                : $"{ex.Message} Geri alma sırasında da sorun oluştu; ayrıntılar değişiklik geçmişinde.";
            return new ApplyResult(false, rolledBack, message);
        }

        var final = entry with
        {
            Status = keepPending ? JournalStatus.Pending : JournalStatus.Applied,
            Notes = notes,
        };
        journal.Save(final);
        return new ApplyResult(true, final);
    }

    public JournalEntry? Confirm(Guid entryId)
    {
        var entry = journal.Get(entryId);
        if (entry is not { Status: JournalStatus.Pending })
            return entry;

        var confirmed = entry with { Status = JournalStatus.Applied };
        journal.Save(confirmed);
        return confirmed;
    }

    /// <summary>
    /// Restores originals. A value that no longer matches what we set is left alone: the
    /// user or Windows changed it afterwards, and overwriting that would be a surprise.
    /// </summary>
    public RevertResult Revert(Guid entryId)
    {
        var entry = journal.Get(entryId);
        if (entry is null)
            return new RevertResult(false, null, "Kayıt bulunamadı.");
        if (!entry.CanRevert)
            return new RevertResult(false, entry, "Bu kayıt zaten kapatılmış.");

        var notes = entry.Notes.ToList();
        var skipped = 0;
        var errors = 0;

        foreach (var change in entry.Changes.Reverse())
        {
            try
            {
                switch (executor.Inspect(change))
                {
                    case ChangeState.Original:
                        continue;
                    case ChangeState.Diverged:
                        skipped++;
                        notes.Add($"Atlandı, sonradan başka bir şey değiştirmiş: {change.Describe()}");
                        continue;
                    default:
                        executor.Revert(change);
                        break;
                }
            }
            catch (Exception ex)
            {
                errors++;
                notes.Add($"Geri alınamadı ({change.Describe()}): {ex.Message}");
            }
        }

        var closed = entry with
        {
            Status = skipped + errors == 0 ? JournalStatus.Reverted : JournalStatus.PartiallyReverted,
            ClosedAt = _clock.GetLocalNow(),
            Notes = notes,
        };
        journal.Save(closed);

        return errors == 0
            ? new RevertResult(true, closed)
            : new RevertResult(false, closed, $"{errors} değişiklik geri alınamadı.");
    }

    /// <summary>
    /// Reverts every entry left Pending by a crash or an unconfirmed change. Entries waiting
    /// for a restart are left to the restart-confirmation flow (<see cref="AwaitingRestart"/>).
    /// </summary>
    public IReadOnlyList<RevertResult> RecoverPending(Func<JournalEntry, bool>? canRevert = null) =>
        journal.LoadAll()
            .Where(e => e.Status == JournalStatus.Pending && !e.RestartRequired && (canRevert?.Invoke(e) ?? true))
            .Select(e => Revert(e.Id))
            .ToList();

    /// <summary>Applied-but-unconfirmed changes that only take effect after a restart.</summary>
    public IReadOnlyList<JournalEntry> AwaitingRestart() =>
        journal.LoadAll().Where(e => e.Status == JournalStatus.Pending && e.RestartRequired).ToList();

    /// <summary>True once Windows has restarted since the entry was applied.</summary>
    public bool HasRestartedSince(JournalEntry entry) =>
        entry.BootTime is { } then && (_bootTime() - then).Duration() > TimeSpan.FromMinutes(1);

    /// <summary>Current state of each change of an entry; null where it cannot be read (e.g. needs admin).</summary>
    public IReadOnlyList<ChangeState?> Inspect(JournalEntry entry) =>
        entry.Changes.Select(c =>
        {
            try
            {
                return (ChangeState?)executor.Inspect(c);
            }
            catch (Exception)
            {
                return null;
            }
        }).ToList();

    /// <summary>Reverts every open entry, newest first. The "undo everything" safety net.</summary>
    public IReadOnlyList<RevertResult> RevertAll() =>
        journal.LoadAll()
            .Where(e => e.CanRevert)
            .Select(e => Revert(e.Id))
            .ToList();

    private List<string> RevertAll(IEnumerable<Change> applied)
    {
        var errors = new List<string>();
        foreach (var change in applied.Reverse())
        {
            try
            {
                executor.Revert(change);
            }
            catch (Exception ex)
            {
                errors.Add($"Geri alınamadı ({change.Describe()}): {ex.Message}");
            }
        }

        return errors;
    }
}
