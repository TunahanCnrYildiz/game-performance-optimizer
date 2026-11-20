using System.Text.Json.Serialization;
using Optimizer.Core.Inventory;

namespace Optimizer.Core.Changes;

/// <summary>
/// One primitive, reversible modification. A change is created by a tweak with only
/// its target ("After") filled in; the executor then captures the current state into
/// "Before" so that the change fully describes how to undo itself. Captured changes
/// are written to the journal before anything is touched.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RegistryValueChange), "registry")]
[JsonDerivedType(typeof(PowerSchemeChange), "powerScheme")]
[JsonDerivedType(typeof(DisplayModeChange), "displayMode")]
[JsonDerivedType(typeof(MouseAccelerationChange), "mouseAcceleration")]
[JsonDerivedType(typeof(ProcessEfficiencyChange), "processEfficiency")]
public abstract record Change
{
    /// <summary>True once the executor has recorded the original state.</summary>
    public bool Captured { get; init; }

    /// <summary>Human-readable "target: before → after" line for the UI and the journal.</summary>
    public abstract string Describe();
}

public enum RegistryDataKind { Dword, Qword, String, ExpandString, MultiString, Binary }

/// <summary>Registry data as text, so it can round-trip through the JSON journal.</summary>
public sealed record RegistryData(RegistryDataKind Kind, string Value)
{
    public static RegistryData Dword(int value) => new(RegistryDataKind.Dword, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static RegistryData String(string value) => new(RegistryDataKind.String, value);

    public override string ToString() => Kind switch
    {
        RegistryDataKind.String or RegistryDataKind.ExpandString => $"\"{Value}\"",
        _ => $"{Value} ({Kind.ToString().ToUpperInvariant()})",
    };
}

/// <param name="Hive">"HKCU" or "HKLM".</param>
/// <param name="Before">Original data; null means the value did not exist.</param>
/// <param name="After">Target data; null means "delete the value".</param>
public sealed record RegistryValueChange(
    string Hive,
    string Key,
    string Name,
    RegistryData? Before,
    RegistryData? After) : Change
{
    /// <summary>Whether the key itself existed before; if not, reverting removes the empty key again.</summary>
    public bool KeyExisted { get; init; } = true;

    public string Path => $@"{Hive}\{Key}\{Name}";

    public override string Describe() =>
        $"{Path}: {(Captured ? Before?.ToString() ?? "(yok)" : "…")} → {After?.ToString() ?? "(silinir)"}";
}

public sealed record PowerSchemeChange(Guid? Before, Guid After, string? AfterName = null) : Change
{
    public override string Describe() => $"Etkin güç planı → {AfterName ?? After.ToString()}";
}

public sealed record DisplayModeChange(string DeviceName, string? MonitorName, DisplayMode? Before, DisplayMode After) : Change
{
    public override string Describe() =>
        $"{MonitorName ?? DeviceName}: {(Before is { } b ? $"{b.Width}×{b.Height} @ {b.RefreshHz} Hz" : "…")} → "
        + $"{After.Width}×{After.Height} @ {After.RefreshHz} Hz";
}

/// <summary>The three SPI_GETMOUSE values: threshold 1, threshold 2, acceleration.</summary>
public sealed record MouseParameters(int Threshold1, int Threshold2, int Acceleration)
{
    public static readonly MouseParameters Off = new(0, 0, 0);
    public static readonly MouseParameters WindowsDefault = new(6, 10, 1);

    public bool AccelerationOn => Acceleration != 0;
}

/// <summary>Priority class and EcoQoS (power throttling) masks of a running process.</summary>
public sealed record ProcessEfficiencyState(int PriorityClass, uint ThrottleControlMask, uint ThrottleStateMask);

/// <summary>
/// Puts one running process into "efficiency mode" (the same thing Task Manager does:
/// idle priority + EcoQoS). Lives only as long as the process; identified by PID and
/// start time so a recycled PID is never touched.
/// </summary>
public sealed record ProcessEfficiencyChange(int Pid, string ProcessName, DateTime StartTimeUtc, ProcessEfficiencyState? Before) : Change
{
    public override string Describe() => $"{ProcessName} (PID {Pid}): verimlilik modu (düşük öncelik + EcoQoS)";
}

public sealed record MouseAccelerationChange(MouseParameters? Before, MouseParameters After) : Change
{
    public override string Describe() =>
        $"İşaretçi hassasiyetini artır: {(Before is null ? "…" : Before.AccelerationOn ? "açık" : "kapalı")} → {(After.AccelerationOn ? "açık" : "kapalı")}";
}
