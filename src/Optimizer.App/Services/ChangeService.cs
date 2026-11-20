using Microsoft.Win32;
using Optimizer.Core.Changes;
using Optimizer.Platform.Windows.Elevation;

namespace Optimizer.App.Services;

public sealed record AdminApplyResult(ApplyResult Result, string? RestorePointError, bool RestorePointCreated);

/// <summary>
/// Single entry point for changing and reverting. Per-user changes run in-process;
/// anything marked <see cref="ITweak.RequiresAdmin"/> / <see cref="JournalEntry.RequiresAdmin"/>
/// goes through a UAC-elevated copy of the app. Both write to the same journal.
/// </summary>
public sealed class ChangeService(TweakEngine local, ElevatedRunner elevated)
{
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceName = "PerformanceOptimizerAfterRestart";

    public Journal Journal => local.Journal;

    public async Task<AdminApplyResult> ApplyAsync(
        ITweak tweak,
        IReadOnlyList<Change> changes,
        bool keepPending = false,
        bool createRestorePoint = false,
        bool requireRestorePoint = false)
    {
        if (!tweak.RequiresAdmin)
        {
            var result = await Task.Run(() => local.Apply(tweak, changes, keepPending || tweak.RequiresRestart));
            AfterApply(tweak, result);
            return new AdminApplyResult(result, null, false);
        }

        var response = await elevated.RunAsync(new ElevatedRequest
        {
            Operation = ElevatedOperation.Apply,
            JournalDirectory = local.Journal.DirectoryPath,
            TweakId = tweak.Id,
            Title = tweak.Title,
            Changes = changes,
            KeepPending = keepPending,
            RequiresRestart = tweak.RequiresRestart,
            CreateRestorePoint = createRestorePoint,
            RequireRestorePoint = requireRestorePoint,
        });

        var mapped = new ApplyResult(
            response.Success,
            response.EntryId is { } id ? local.Journal.Get(id) : null,
            response.Error,
            response.NothingToDo);
        AfterApply(tweak, mapped);
        return new AdminApplyResult(mapped, response.RestorePointError, response.RestorePointCreated);
    }

    public async Task<RevertResult> RevertAsync(Guid entryId)
    {
        var entry = local.Journal.Get(entryId);
        if (entry is null)
            return new RevertResult(false, null, "Kayıt bulunamadı.");

        RevertResult result;
        if (entry.RequiresAdmin && !ElevatedHost.IsElevated())
        {
            var response = await elevated.RunAsync(new ElevatedRequest
            {
                Operation = ElevatedOperation.Revert,
                JournalDirectory = local.Journal.DirectoryPath,
                EntryId = entryId,
            });
            result = new RevertResult(response.Success, local.Journal.Get(entryId), response.Error);
        }
        else
        {
            result = await Task.Run(() => local.Revert(entryId));
        }

        UpdateRunOnce();
        return result;
    }

    public async Task<IReadOnlyList<RevertResult>> RevertAllAsync()
    {
        var results = new List<RevertResult>();
        foreach (var entry in local.Journal.LoadAll().Where(e => e.CanRevert))
            results.Add(await RevertAsync(entry.Id));
        return results;
    }

    /// <summary>
    /// Startup: reverts what a crash left Pending. Admin entries are included (one UAC prompt each);
    /// entries waiting for a restart are not — they go through the restart confirmation instead.
    /// </summary>
    public async Task<IReadOnlyList<RevertResult>> RecoverPendingAsync()
    {
        var results = (await Task.Run(() => local.RecoverPending(canRevert: e => !e.RequiresAdmin))).ToList();
        foreach (var entry in local.Journal.LoadAll().Where(e => e is { Status: JournalStatus.Pending, RequiresAdmin: true, RestartRequired: false }))
            results.Add(await RevertAsync(entry.Id));
        return results;
    }

    public JournalEntry? Confirm(Guid entryId)
    {
        var confirmed = local.Confirm(entryId);
        UpdateRunOnce();
        return confirmed;
    }

    public IReadOnlyList<JournalEntry> AwaitingRestart() => local.AwaitingRestart();

    public bool HasRestartedSince(JournalEntry entry) => local.HasRestartedSince(entry);

    public IReadOnlyList<ChangeState?> Inspect(JournalEntry entry) => local.Inspect(entry);

    private void AfterApply(ITweak tweak, ApplyResult result)
    {
        if (tweak.RequiresRestart && result is { Success: true, NothingToDo: false })
            UpdateRunOnce();
    }

    /// <summary>
    /// While a change waits for a restart, the app is registered to open once at the next sign-in
    /// so the user can confirm or undo it. Windows removes RunOnce entries after running them.
    /// </summary>
    private void UpdateRunOnce()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey, writable: true);
            if (local.AwaitingRestart().Count > 0 && Environment.ProcessPath is { } exe)
                key.SetValue(RunOnceName, $"\"{exe}\" --after-restart");
            else
                key.DeleteValue(RunOnceName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Not critical: the user can still open the app manually after restarting.
        }
    }
}
