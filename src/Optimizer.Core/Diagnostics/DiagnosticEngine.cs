using Optimizer.Core.Diagnostics.Rules;
using Optimizer.Core.Inventory;

namespace Optimizer.Core.Diagnostics;

public sealed class DiagnosticEngine(IEnumerable<IRule> rules)
{
    private readonly IReadOnlyList<IRule> _rules = rules.ToList();

    public static DiagnosticEngine CreateDefault() => new(
    [
        new RefreshRateRule(),
        new MonitorOnIntegratedGpuRule(),
        new HybridGpuRule(),
        new GpuDriverAgeRule(),
        new MemorySpeedRule(),
        new MemoryChannelRule(),
        new PowerRule(),
        new GameModeRule(),
        new BackgroundRecordingRule(),
        new HagsRule(),
        new WindowedOptimizationsRule(),
        new PointerPrecisionRule(),
        new MemoryIntegrityRule(),
        new SecureBootRule(),
        new DiskSpaceRule(),
        new GamesOnHddRule(),
        new StartupItemsRule(),
    ]);

    /// <summary>Platform-neutral rules plus Linux-specific ones; Windows-only rules are left out.</summary>
    public static DiagnosticEngine CreateForLinux() => new(
    [
        new RefreshRateRule(),
        new MemoryChannelRule(),
        new PowerRule(),
        new DiskSpaceRule(),
        new GamesOnHddRule(),
        new LinuxPowerProfileRule(),
        new LinuxCpuGovernorRule(),
        new LinuxMaxMapCountRule(),
        new LinuxGameModeRule(),
        new LinuxSplitLockRule(),
    ]);

    /// <summary>
    /// Runs every rule. A rule that throws is reported as a finding instead of
    /// aborting the whole scan.
    /// </summary>
    public IReadOnlyList<Finding> Evaluate(SystemInventory inventory)
    {
        var findings = new List<Finding>();
        foreach (var rule in _rules)
        {
            try
            {
                findings.AddRange(rule.Evaluate(inventory));
            }
            catch (Exception ex)
            {
                findings.Add(new Finding
                {
                    Id = $"engine.rule-failed.{rule.Id}",
                    Category = Category.Windows,
                    Severity = Severity.Info,
                    Title = $"'{rule.Id}' kontrolü çalıştırılamadı",
                    Detail = ex.Message,
                });
            }
        }

        return findings
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.Impact)
            .ThenBy(f => f.Category)
            .ToList();
    }
}
