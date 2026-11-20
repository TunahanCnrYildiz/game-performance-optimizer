using Microsoft.Win32;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Inventory;
using Optimizer.Platform.Windows.Changes;

namespace Optimizer.Platform.Windows.Tests;

/// <summary>
/// Runs against the real registry, but only under a throw-away key in HKCU that is
/// deleted afterwards. Never touches any real setting.
/// </summary>
public sealed class RegistryExecutorTests : IDisposable
{
    private const string Root = @"Software\PerformanceOptimizerTests";
    private readonly string _key = $@"{Root}\{Guid.NewGuid():N}";
    private readonly string _journalDir = Path.Combine(Path.GetTempPath(), "optimizer-tests", Guid.NewGuid().ToString("N"));
    private readonly WindowsChangeExecutor _executor = new();

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
        using (var root = Registry.CurrentUser.OpenSubKey(Root))
        {
            if (root is { SubKeyCount: 0, ValueCount: 0 })
            {
                root.Dispose();
                Registry.CurrentUser.DeleteSubKey(Root, throwOnMissingSubKey: false);
            }
        }

        try { Directory.Delete(_journalDir, recursive: true); } catch (IOException) { }
    }

    private RegistryValueChange Change(string name, RegistryData? after) => new("HKCU", _key, name, null, after);

    private object? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_key);
        return key?.GetValue(name);
    }

    private bool KeyExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_key);
        return key is not null;
    }

    [Fact]
    public void Missing_key_is_created_and_removed_again_on_revert()
    {
        var captured = (RegistryValueChange)_executor.Capture(Change("Value", RegistryData.Dword(1)));
        Assert.False(captured.KeyExisted);
        Assert.Null(captured.Before);

        _executor.Apply(captured);
        Assert.Equal(1, Read("Value"));
        Assert.Equal(ChangeState.Applied, _executor.Inspect(captured));

        _executor.Revert(captured);
        Assert.False(KeyExists());
    }

    [Fact]
    public void Existing_values_of_every_kind_round_trip()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_key))
        {
            key.SetValue("dw", 7, RegistryValueKind.DWord);
            key.SetValue("qw", 1L << 40, RegistryValueKind.QWord);
            key.SetValue("s", "metin; ş ğ ü", RegistryValueKind.String);
            key.SetValue("x", "%TEMP%\\a", RegistryValueKind.ExpandString);
            key.SetValue("m", new[] { "a", "b" }, RegistryValueKind.MultiString);
            key.SetValue("b", new byte[] { 2, 0, 0, 255 }, RegistryValueKind.Binary);
        }

        foreach (var name in new[] { "dw", "qw", "s", "x", "m", "b" })
        {
            var captured = (RegistryValueChange)_executor.Capture(Change(name, RegistryData.String("geçici")));
            _executor.Apply(captured);
            Assert.Equal("geçici", Read(name));
            _executor.Revert(captured);
        }

        Assert.Equal(7, Read("dw"));
        Assert.Equal(1L << 40, Read("qw"));
        Assert.Equal("metin; ş ğ ü", Read("s"));
        using var check = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal("%TEMP%\\a", check.GetValue("x", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
        Assert.Equal(new[] { "a", "b" }, Read("m"));
        Assert.Equal(new byte[] { 2, 0, 0, 255 }, Read("b"));
    }

    [Fact]
    public void Engine_end_to_end_with_journal_and_reg_backup()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_key))
            key.SetValue("Mode", 0, RegistryValueKind.DWord);

        var engine = new TweakEngine(_executor, new Journal(_journalDir), "test");
        var result = engine.Apply(new TestTweak(), [Change("Mode", RegistryData.Dword(1))]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, Read("Mode"));
        var backups = Directory.GetFiles(engine.Journal.BackupDirectoryFor(result.Entry!.Id), "*.reg");
        Assert.Single(backups);
        Assert.Contains("Mode", File.ReadAllText(backups[0]));

        engine.Revert(result.Entry.Id);
        Assert.Equal(0, Read("Mode"));
    }

    [Fact]
    public void Windowed_optimizations_pair_update_keeps_other_settings()
    {
        Assert.Equal("SwapEffectUpgradeEnable=1;", WindowedOptimizationsTweak.SetPair(null, "SwapEffectUpgradeEnable", "1"));
        Assert.Equal(
            "VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;",
            WindowedOptimizationsTweak.SetPair("VRROptimizeEnable=0;SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable", "1"));
        Assert.Equal(
            "AutoHDREnable=1;SwapEffectUpgradeEnable=1;",
            WindowedOptimizationsTweak.SetPair("AutoHDREnable=1;", "SwapEffectUpgradeEnable", "1"));
    }

    [Fact]
    public void Catalog_offers_no_fix_for_ok_findings()
    {
        var catalog = new WindowsTweakCatalog();
        var ok = new Finding { Id = "windows.game-mode", Category = Category.Windows, Severity = Severity.Ok, Title = "" };
        var bad = ok with { Severity = Severity.Suggestion };

        Assert.Null(catalog.FindFor(ok));
        Assert.Equal("windows.game-mode.on", catalog.FindFor(bad)?.Id);
    }

    private sealed class TestTweak : ITweak
    {
        public string Id => "test";
        public string Title => "Test";
        public Risk Risk => Risk.Low;
        public bool Handles(Finding finding) => false;
        public IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) => [];
    }
}
