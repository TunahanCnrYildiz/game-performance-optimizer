using Optimizer.Core.Diagnostics;
using Optimizer.Core.Inventory;

namespace Optimizer.Core.Changes;

public enum Risk { Low, Medium, High }

/// <summary>A fix for a finding, expressed as a list of reversible changes.</summary>
public interface ITweak
{
    string Id { get; }
    string Title { get; }
    Risk Risk { get; }

    /// <summary>
    /// The user must confirm the result within a timeout (e.g. a display mode that might
    /// leave the screen black); otherwise it is reverted automatically.
    /// </summary>
    bool RequiresConfirmation => false;

    /// <summary>Needs administrator rights; applied through the elevated helper.</summary>
    bool RequiresAdmin => false;

    /// <summary>Takes effect after a restart; confirmed (or reverted) after the next boot.</summary>
    bool RequiresRestart => false;

    bool Handles(Finding finding);

    /// <summary>Target changes, uncaptured. Empty when there is nothing to do.</summary>
    IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding);
}

/// <summary>A tweak triggered from the Advanced tab rather than from a finding.</summary>
public sealed record ManualTweak(string Id, string Title, Risk Risk, bool RequiresAdmin = false, bool RequiresRestart = false) : ITweak
{
    public bool Handles(Finding finding) => false;

    public IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding) => [];
}

public interface ITweakCatalog
{
    ITweak? FindFor(Finding finding);
}
