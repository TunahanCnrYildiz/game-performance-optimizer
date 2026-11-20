using System.Collections.Concurrent;
using Optimizer.Core.Inventory;
using Optimizer.Platform.Windows.Collectors;

namespace Optimizer.Platform.Windows;

/// <summary>
/// Read-only scan of a Windows machine. Needs no administrator rights. Each
/// section is collected independently; a failure becomes a <see cref="CollectorError"/>
/// and the rest of the scan continues.
/// </summary>
public sealed class WindowsInventoryCollector : IInventoryCollector
{
    public async Task<SystemInventory> CollectAsync(CancellationToken cancellationToken = default)
    {
        var errors = new ConcurrentBag<CollectorError>();

        Task<T?> Run<T>(string name, Func<T> collect) where T : class =>
            Task.Run(() =>
            {
                try
                {
                    return collect();
                }
                catch (Exception ex)
                {
                    errors.Add(new CollectorError(name, ex.Message));
                    return null;
                }
            }, cancellationToken);

        var os = Run("İşletim sistemi", WmiCollectors.CollectOs);
        var cpu = Run("İşlemci", WmiCollectors.CollectCpu);
        var gpus = Run("Ekran kartı", WmiCollectors.CollectGpus);
        var memory = Run("Bellek", WmiCollectors.CollectMemory);
        var displays = Run("Ekranlar", DisplayCollector.Collect);
        var power = Run("Güç", PowerCollector.Collect);
        var gaming = Run("Oyun ayarları", RegistryCollectors.CollectGaming);
        var security = Run("Güvenlik", WmiCollectors.CollectSecurity);
        var disks = Run("Diskler", DiskCollector.Collect);
        var startup = Run("Başlangıç programları", RegistryCollectors.CollectStartupItems);

        await Task.WhenAll(os, cpu, gpus, memory, displays, power, gaming, security, disks, startup);

        return new SystemInventory
        {
            CollectedAt = DateTimeOffset.Now,
            Os = os.Result,
            Cpu = cpu.Result,
            Gpus = gpus.Result ?? [],
            Memory = memory.Result,
            Displays = displays.Result ?? [],
            Power = power.Result,
            Gaming = gaming.Result,
            Security = security.Result,
            Disks = disks.Result ?? [],
            StartupItems = startup.Result ?? [],
            Errors = errors.OrderBy(e => e.Collector).ToList(),
        };
    }
}
