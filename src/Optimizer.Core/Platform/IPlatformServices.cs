using Optimizer.Core.Benchmarks;
using Optimizer.Core.Changes;
using Optimizer.Core.Diagnostics;
using Optimizer.Core.Elevation;
using Optimizer.Core.Inventory;
using Optimizer.Core.Sessions;

namespace Optimizer.Core.Platform;

public sealed record PlatformPaths(string Root)
{
    public string Journal => Path.Combine(Root, "journal");
    public string Sessions => Path.Combine(Root, "sessions");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Benchmarks => Path.Combine(Root, "benchmarks");
}

/// <summary>Everything the UI and CLI need from an operating system, behind one seam.</summary>
public interface IPlatformServices
{
    /// <summary>"Windows" or "Linux".</summary>
    string Name { get; }

    PlatformPaths Paths { get; }
    IInventoryCollector Inventory { get; }
    DiagnosticEngine CreateDiagnostics();
    ITweakCatalog Tweaks { get; }
    IChangeExecutor Executor { get; }
    ISystemSampler CreateSampler();
    IReadOnlyList<string> FindGameRoots();
    IFrameCapture FrameCapture { get; }
    IElevatedRunner Elevation { get; }
    IAdvancedProvider Advanced { get; }

    /// <summary>Whether background apps can be throttled during a game and restored afterwards.</summary>
    bool SupportsEfficiencyMode { get; }

    /// <summary>Opens the app once at the next sign-in while a change waits for a restart (or removes that hook).</summary>
    void UpdateRestartHook(bool needed);

    void RestartComputer(int delaySeconds);

    /// <summary>Runs the elevated helper for <c>--elevated request response</c>; returns the exit code.</summary>
    int RunElevatedHelper(string requestPath, string responsePath);
}

public sealed record AdvancedAction(
    string Label,
    ITweak Tweak,
    IReadOnlyList<Change> Changes,
    string Explanation,
    bool Enabled = true,
    string? ExtraConfirm = null);

public sealed record AdvancedSubItem(string Name, string Detail, string State, AdvancedAction? Action);

public sealed record AdvancedGroup(
    string Title,
    string Description,
    string State,
    bool IsSecuritySensitive,
    IReadOnlyList<AdvancedAction> Actions,
    IReadOnlyList<AdvancedSubItem> SubItems,
    IReadOnlyList<string> Lines);

/// <summary>Administrator-level settings offered on the Advanced tab of a platform.</summary>
public interface IAdvancedProvider
{
    bool SupportsRestorePoint { get; }

    /// <param name="journal">Current journal, to show pending restarts and changes this app made.</param>
    IReadOnlyList<AdvancedGroup> GetGroups(IReadOnlyList<JournalEntry> journal);
}
