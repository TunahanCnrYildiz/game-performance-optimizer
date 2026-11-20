namespace Optimizer.Core.Inventory;

/// <summary>Platform-specific, read-only system scan.</summary>
public interface IInventoryCollector
{
    Task<SystemInventory> CollectAsync(CancellationToken cancellationToken = default);
}
