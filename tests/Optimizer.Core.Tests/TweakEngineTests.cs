using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Inventory;

namespace Optimizer.Core.Tests;

public sealed class TweakEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "optimizer-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _registry = new();
    private readonly Journal _journal;
    private readonly TweakEngine _engine;

    public TweakEngineTests()
    {
        _journal = new Journal(_dir);
        _engine = new TweakEngine(_registry, _journal, "test");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static RegistryValueChange Set(string name, int value) =>
        new("HKCU", @"Software\Test", name, null, RegistryData.Dword(value));

    private sealed class Tweak : ITweak
    {
        public string Id => "test.tweak";
        public string Title => "Test";
        public Risk Risk => Risk.Low;
        public bool Handles(Finding finding) => true;
        public IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) => [];
    }

    [Fact]
    public void Apply_records_original_and_sets_value()
    {
        _registry.Values["A"] = RegistryData.Dword(0);

        var result = _engine.Apply(new Tweak(), [Set("A", 1)]);

        Assert.True(result.Success);
        Assert.Equal("1", _registry.Values["A"].Value);
        var entry = Assert.Single(_journal.LoadAll());
        Assert.Equal(JournalStatus.Applied, entry.Status);
        var change = Assert.IsType<RegistryValueChange>(Assert.Single(entry.Changes));
        Assert.Equal("0", change.Before!.Value);
    }

    [Fact]
    public void Revert_restores_original_and_deletes_values_that_did_not_exist()
    {
        _registry.Values["A"] = RegistryData.Dword(0);
        var result = _engine.Apply(new Tweak(), [Set("A", 1), Set("B", 5)]);

        var revert = _engine.Revert(result.Entry!.Id);

        Assert.True(revert.Success);
        Assert.Equal("0", _registry.Values["A"].Value);
        Assert.False(_registry.Values.ContainsKey("B"));
        Assert.Equal(JournalStatus.Reverted, _journal.Get(result.Entry.Id)!.Status);
    }

    [Fact]
    public void Already_applied_values_are_not_journaled()
    {
        _registry.Values["A"] = RegistryData.Dword(1);

        var result = _engine.Apply(new Tweak(), [Set("A", 1)]);

        Assert.True(result.NothingToDo);
        Assert.Empty(_journal.LoadAll());
    }

    [Fact]
    public void Failure_midway_rolls_back_earlier_changes()
    {
        _registry.Values["A"] = RegistryData.Dword(0);
        _registry.FailOn = "B";

        var result = _engine.Apply(new Tweak(), [Set("A", 1), Set("B", 1)]);

        Assert.False(result.Success);
        Assert.Equal("0", _registry.Values["A"].Value);
        Assert.False(_registry.Values.ContainsKey("B"));
        Assert.Equal(JournalStatus.RolledBack, _journal.Get(result.Entry!.Id)!.Status);
    }

    [Fact]
    public void Verification_failure_rolls_back()
    {
        _registry.Values["A"] = RegistryData.Dword(0);
        _registry.SilentlyIgnore = "A"; // write "succeeds" but nothing changes

        var result = _engine.Apply(new Tweak(), [Set("A", 1)]);

        Assert.False(result.Success);
        Assert.Contains("Doğrulama", result.Error);
    }

    [Fact]
    public void Journal_is_written_before_the_first_change()
    {
        JournalStatus? statusSeenDuringApply = null;
        _registry.OnApply = () => statusSeenDuringApply = _journal.LoadAll().Single().Status;

        _engine.Apply(new Tweak(), [Set("A", 1)]);

        Assert.Equal(JournalStatus.Pending, statusSeenDuringApply);
    }

    [Fact]
    public void Revert_skips_values_changed_by_someone_else()
    {
        _registry.Values["A"] = RegistryData.Dword(0);
        var result = _engine.Apply(new Tweak(), [Set("A", 1)]);
        _registry.Values["A"] = RegistryData.Dword(7); // user changed it afterwards

        var revert = _engine.Revert(result.Entry!.Id);

        Assert.True(revert.Success);
        Assert.Equal("7", _registry.Values["A"].Value);
        Assert.Equal(JournalStatus.PartiallyReverted, revert.Entry!.Status);
    }

    [Fact]
    public void Pending_entries_are_reverted_on_recovery()
    {
        _registry.Values["A"] = RegistryData.Dword(0);
        _engine.Apply(new Tweak(), [Set("A", 1)], keepPending: true);

        // Simulate a restart: a fresh engine over the same journal.
        var results = new TweakEngine(_registry, new Journal(_dir), "test").RecoverPending();

        Assert.Single(results);
        Assert.Equal("0", _registry.Values["A"].Value);
    }

    [Fact]
    public void Confirmed_entries_survive_recovery()
    {
        _registry.Values["A"] = RegistryData.Dword(0);
        var result = _engine.Apply(new Tweak(), [Set("A", 1)], keepPending: true);
        _engine.Confirm(result.Entry!.Id);

        Assert.Empty(_engine.RecoverPending());
        Assert.Equal("1", _registry.Values["A"].Value);
    }

    [Fact]
    public void RevertAll_undoes_everything_newest_first()
    {
        _registry.Values["A"] = RegistryData.Dword(0);
        _engine.Apply(new Tweak(), [Set("A", 1)]);
        _engine.Apply(new Tweak(), [Set("A", 2)]);

        _engine.RevertAll();

        Assert.Equal("0", _registry.Values["A"].Value);
    }

    [Fact]
    public void Corrupt_journal_files_are_skipped_not_deleted()
    {
        var bad = Path.Combine(_dir, "broken.json");
        File.WriteAllText(bad, "{ not json");

        Assert.Empty(_journal.LoadAll());
        Assert.True(File.Exists(bad));
    }

    /// <summary>In-memory registry: one key, values by name.</summary>
    private sealed class FakeRegistry : IChangeExecutor
    {
        public Dictionary<string, RegistryData> Values { get; } = [];
        public string? FailOn { get; set; }
        public string? SilentlyIgnore { get; set; }
        public Action? OnApply { get; set; }

        public Change Capture(Change desired)
        {
            var c = (RegistryValueChange)desired;
            return c with { Before = Values.GetValueOrDefault(c.Name), Captured = true };
        }

        public void Apply(Change change)
        {
            OnApply?.Invoke();
            var c = (RegistryValueChange)change;
            if (c.Name == FailOn)
                throw new UnauthorizedAccessException("denied");
            if (c.Name == SilentlyIgnore)
                return;
            Set(c.Name, c.After);
        }

        public void Revert(Change change)
        {
            var c = (RegistryValueChange)change;
            Set(c.Name, c.Before);
        }

        public ChangeState Inspect(Change change)
        {
            var c = (RegistryValueChange)change;
            var current = Values.GetValueOrDefault(c.Name);
            if (current == c.After) return ChangeState.Applied;
            if (current == c.Before) return ChangeState.Original;
            return ChangeState.Diverged;
        }

        private void Set(string name, RegistryData? data)
        {
            if (data is null) Values.Remove(name);
            else Values[name] = data;
        }
    }
}
