using Optimizer.Core.Changes;
using Optimizer.Core.Inventory;
using Optimizer.Platform.Windows.Changes;
using Optimizer.Platform.Windows.Elevation;

namespace Optimizer.Platform.Windows.Tests;

/// <summary>The elevated helper's allowlist and request handling, without actually elevating.</summary>
public sealed class ElevationTests : IDisposable
{
    private const string Library = @"D:\SteamLibrary\steamapps\common";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "optimizer-tests", Guid.NewGuid().ToString("N"));
    private readonly ElevatedChangePolicy _policy = new([Library]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly StartupItem MachineItem = new(
        "Vendor", "vendor.exe", "Tüm kullanıcılar (Run)", true,
        "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "Vendor");

    [Fact]
    public void Allowlisted_changes_pass()
    {
        Assert.True(_policy.IsAllowed(AdvancedSettings.HagsChange(true)));
        Assert.True(_policy.IsAllowed(AdvancedSettings.HvciChange(false)));
        Assert.True(_policy.IsAllowed(AdvancedSettings.StartupChange(MachineItem, enable: false)));
        Assert.True(_policy.IsAllowed(new DefenderExclusionChange(Library + @"\", null)));
    }

    [Theory]
    [MemberData(nameof(Forbidden))]
    public void Everything_else_is_rejected(Change change) => Assert.False(_policy.IsAllowed(change));

    public static TheoryData<Change> Forbidden() =>
    [
        // Other values in allowed keys, other keys, other hives.
        new RegistryValueChange("HKLM", AdvancedSettings.GraphicsDriversKey, "TdrDelay", null, RegistryData.Dword(60)),
        new RegistryValueChange("HKLM", AdvancedSettings.GraphicsDriversKey, "HwSchMode", null, RegistryData.Dword(7)),
        new RegistryValueChange("HKLM", AdvancedSettings.HvciKey, "Enabled", null, RegistryData.String("0")),
        new RegistryValueChange("HKLM", @"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", null, RegistryData.Dword(1)),
        new RegistryValueChange("HKLM", @"SYSTEM\CurrentControlSet\Services\WinDefend", "Start", null, RegistryData.Dword(4)),
        new RegistryValueChange("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run", "evil", null, RegistryData.String("evil.exe")),
        new RegistryValueChange("HKCU", AdvancedSettings.GraphicsDriversKey, "HwSchMode", null, RegistryData.Dword(2)),
        // Startup blob with a forged payload.
        new RegistryValueChange("HKLM", @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "x", null,
            new RegistryData(RegistryDataKind.Binary, "FF")),
        // Defender exclusions outside the game libraries.
        new DefenderExclusionChange(@"C:\", null),
        new DefenderExclusionChange(@"C:\Users\someone\Downloads", null),
        // Change types the helper never handles.
        new PowerSchemeChange(null, Guid.Empty),
        new ProcessEfficiencyChange(4, "System", DateTime.UtcNow, null),
    ];

    [Fact]
    public void Request_with_a_forbidden_change_touches_nothing()
    {
        var executor = new RecordingExecutor();
        var request = new ElevatedRequest
        {
            Operation = ElevatedOperation.Apply,
            JournalDirectory = _dir,
            TweakId = "x",
            Title = "x",
            Changes = [AdvancedSettings.HagsChange(true), new DefenderExclusionChange(@"C:\", null)],
        };

        var response = ElevatedHost.Execute(request, executor, _policy, _ => null);

        Assert.False(response.Success);
        Assert.Contains("reddedildi", response.Error);
        Assert.Empty(executor.Applied);
        Assert.Empty(new Journal(_dir).LoadAll());
    }

    [Fact]
    public void Restart_tweak_is_journaled_pending_with_admin_and_restart_flags()
    {
        var executor = new RecordingExecutor();
        var request = new ElevatedRequest
        {
            Operation = ElevatedOperation.Apply,
            JournalDirectory = _dir,
            TweakId = AdvancedSettings.HagsOn.Id,
            Title = AdvancedSettings.HagsOn.Title,
            Changes = [AdvancedSettings.HagsChange(true)],
            RequiresRestart = true,
        };

        var response = ElevatedHost.Execute(request, executor, _policy, _ => null);

        Assert.True(response.Success, response.Error);
        var entry = Assert.Single(new Journal(_dir).LoadAll());
        Assert.Equal(JournalStatus.Pending, entry.Status);
        Assert.True(entry.RequiresAdmin);
        Assert.True(entry.RestartRequired);
    }

    [Fact]
    public void Missing_restore_point_aborts_only_when_required()
    {
        ElevatedRequest Request(bool require) => new()
        {
            Operation = ElevatedOperation.Apply,
            JournalDirectory = _dir,
            TweakId = "x",
            Title = "x",
            Changes = [AdvancedSettings.HagsChange(true)],
            CreateRestorePoint = true,
            RequireRestorePoint = require,
        };

        var required = ElevatedHost.Execute(Request(true), new RecordingExecutor(), _policy, _ => "Sistem Geri Yükleme kapalı.");
        Assert.False(required.Success);
        Assert.Equal("Sistem Geri Yükleme kapalı.", required.RestorePointError);

        var optional = ElevatedHost.Execute(Request(false), new RecordingExecutor(), _policy, _ => "Sistem Geri Yükleme kapalı.");
        Assert.True(optional.Success);
        Assert.False(optional.RestorePointCreated);
    }

    [Fact]
    public void Tampered_journal_entry_is_not_reverted()
    {
        var journal = new Journal(_dir);
        var forged = new JournalEntry
        {
            Id = Guid.NewGuid(),
            TweakId = "forged",
            Title = "forged",
            CreatedAt = DateTimeOffset.Now,
            AppVersion = "x",
            Status = JournalStatus.Applied,
            Changes =
            [
                new RegistryValueChange("HKLM", @"SYSTEM\CurrentControlSet\Services\WinDefend", "Start",
                    RegistryData.Dword(4), RegistryData.Dword(2)) { Captured = true },
            ],
        };
        journal.Save(forged);
        var executor = new RecordingExecutor();

        var response = ElevatedHost.Execute(
            new ElevatedRequest { Operation = ElevatedOperation.Revert, JournalDirectory = _dir, EntryId = forged.Id },
            executor, _policy, _ => null);

        Assert.False(response.Success);
        Assert.Empty(executor.Reverted);
    }

    [Fact]
    public void Startup_change_uses_task_manager_blob()
    {
        var change = (RegistryValueChange)AdvancedSettings.StartupChange(MachineItem, enable: false);

        Assert.Equal(RegistryDataKind.Binary, change.After!.Kind);
        Assert.Equal("030000000000000000000000", change.After.Value);
        Assert.Equal("HKLM", change.Hive);
    }

    private sealed class RecordingExecutor : IChangeExecutor
    {
        public List<Change> Applied { get; } = [];
        public List<Change> Reverted { get; } = [];
        private readonly HashSet<Change> _state = [];

        public Change Capture(Change desired) => desired switch
        {
            RegistryValueChange r => r with { Captured = true },
            DefenderExclusionChange d => d with { ExistedBefore = false, Captured = true },
            _ => desired with { Captured = true },
        };

        public void Apply(Change change)
        {
            Applied.Add(change);
            _state.Add(change);
        }

        public void Revert(Change change)
        {
            Reverted.Add(change);
            _state.Remove(change);
        }

        public ChangeState Inspect(Change change) => _state.Contains(change) ? ChangeState.Applied : ChangeState.Original;
    }
}
