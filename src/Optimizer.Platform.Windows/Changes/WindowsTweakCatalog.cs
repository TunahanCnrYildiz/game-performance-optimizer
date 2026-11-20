using Microsoft.Win32;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Inventory;

namespace Optimizer.Platform.Windows.Changes;

/// <summary>
/// Phase 2 tweaks: only low-risk, per-user settings that Windows itself exposes in
/// Settings. None of them needs administrator rights.
/// </summary>
public sealed class WindowsTweakCatalog : ITweakCatalog
{
    private readonly IReadOnlyList<ITweak> _tweaks =
    [
        new RefreshRateTweak(),
        new GameModeTweak(),
        new BackgroundRecordingTweak(),
        new WindowedOptimizationsTweak(),
        new PointerPrecisionTweak(),
        new PowerSaverTweak(),
    ];

    public IReadOnlyList<ITweak> All => _tweaks;

    public ITweak? FindFor(Finding finding) =>
        finding.Severity > Severity.Ok ? _tweaks.FirstOrDefault(t => t.Handles(finding)) : null;
}

internal abstract class SimpleTweak(string id, string findingId, string title) : ITweak
{
    public string Id => id;
    public string Title => title;
    public virtual Risk Risk => Risk.Low;

    public virtual bool Handles(Finding finding) => finding.Id == findingId;

    public abstract IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding);
}

internal sealed class GameModeTweak() : SimpleTweak("windows.game-mode.on", "windows.game-mode", "Oyun Modu'nu aç")
{
    public override IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) =>
        [new RegistryValueChange("HKCU", @"Software\Microsoft\GameBar", "AutoGameModeEnabled", null, RegistryData.Dword(1))];

    // Only offered where Game Mode is actually needed (dual-CCD X3D); elsewhere it is neutral.
    public override bool Handles(Finding finding) => base.Handles(finding) && finding.Severity == Severity.Warning;
}

internal sealed class BackgroundRecordingTweak()
    : SimpleTweak("windows.background-recording.off", "windows.background-recording", "Arka planda oyun kaydını kapat")
{
    public override IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) =>
        [new RegistryValueChange("HKCU", @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", null, RegistryData.Dword(0))];
}

internal sealed class WindowedOptimizationsTweak()
    : SimpleTweak("windows.windowed-optimizations.on", "windows.windowed-optimizations", "Pencereli oyunlar için iyileştirmeleri aç")
{
    private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string Name = "DirectXUserGlobalSettings";

    public override IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding)
    {
        // The value holds several "Key=Value;" settings; change only ours and keep the rest.
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        var current = key?.GetValue(Name) as string;
        var updated = SetPair(current, "SwapEffectUpgradeEnable", "1");
        return [new RegistryValueChange("HKCU", Key, Name, null, RegistryData.String(updated))];
    }

    internal static string SetPair(string? raw, string name, string value)
    {
        var parts = (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var index = parts.FindIndex(p => p.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            parts[index] = $"{name}={value}";
        else
            parts.Add($"{name}={value}");
        return string.Join(";", parts) + ";";
    }
}

internal sealed class PointerPrecisionTweak()
    : SimpleTweak("windows.pointer-precision.off", "windows.pointer-precision", "Fare ivmesini kapat")
{
    public override IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) =>
        [new MouseAccelerationChange(null, MouseParameters.Off)];
}

internal sealed class PowerSaverTweak() : SimpleTweak("power.scheme.balanced", "power.scheme", "Dengeli güç planına geç")
{
    public override IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) =>
        [new PowerSchemeChange(null, PowerInfo.Balanced, "Dengeli")];
}

internal sealed class RefreshRateTweak : ITweak
{
    private const string Prefix = "display.refresh-rate:";

    public string Id => "display.refresh-rate.max";
    public string Title => "Ekranı en yüksek yenileme hızına ayarla";
    public Risk Risk => Risk.Low;
    public bool RequiresConfirmation => true;

    // Only the "running below what it supports" case; the cable/port case cannot be fixed in software.
    public bool Handles(Finding finding) =>
        finding.Id.StartsWith(Prefix, StringComparison.Ordinal) && finding.Severity == Severity.Warning;

    public IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding)
    {
        var device = finding.Id[Prefix.Length..];
        var display = inventory.Displays.FirstOrDefault(d => d.DeviceName == device);
        if (display is null || display.MaxRefreshHzAtCurrentResolution <= display.Current.RefreshHz)
            return [];

        return
        [
            new DisplayModeChange(
                display.DeviceName,
                display.MonitorName,
                null,
                display.Current with { RefreshHz = display.MaxRefreshHzAtCurrentResolution }),
        ];
    }
}
