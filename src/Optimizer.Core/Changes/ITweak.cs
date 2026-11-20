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

    bool Handles(Finding finding);

    /// <summary>Target changes, uncaptured. Empty when there is nothing to do.</summary>
    IReadOnlyList<Change> GetChanges(SystemInventory inventory, Finding finding);
}

public interface ITweakCatalog
{
    ITweak? FindFor(Finding finding);
}
