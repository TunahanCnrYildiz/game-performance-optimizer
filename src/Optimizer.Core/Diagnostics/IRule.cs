using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics;

/// <summary>
/// A pure function of the inventory. Rules must not touch the system; that keeps
/// them unit-testable and guarantees the diagnostic phase is read-only.
/// </summary>
public interface IRule
{
    string Id { get; }

    IEnumerable<Finding> Evaluate(SystemInventory inventory);
}
