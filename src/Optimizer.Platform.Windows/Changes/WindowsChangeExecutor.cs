using Optimizer.Core.Changes;

namespace Optimizer.Platform.Windows.Changes;

public sealed class WindowsChangeExecutor : IChangeExecutor
{
    public Change Capture(Change desired) => desired switch
    {
        RegistryValueChange r => RegistryExecutor.Capture(r),
        PowerSchemeChange p => PowerSchemeExecutor.Capture(p),
        DisplayModeChange d => DisplayModeExecutor.Capture(d),
        MouseAccelerationChange m => MouseExecutor.Capture(m),
        _ => throw Unsupported(desired),
    };

    public void Apply(Change change)
    {
        switch (change)
        {
            case RegistryValueChange r: RegistryExecutor.Apply(r); break;
            case PowerSchemeChange p: PowerSchemeExecutor.Apply(p); break;
            case DisplayModeChange d: DisplayModeExecutor.Apply(d); break;
            case MouseAccelerationChange m: MouseExecutor.Apply(m); break;
            default: throw Unsupported(change);
        }
    }

    public void Revert(Change change)
    {
        switch (change)
        {
            case RegistryValueChange r: RegistryExecutor.Revert(r); break;
            case PowerSchemeChange p: PowerSchemeExecutor.Revert(p); break;
            case DisplayModeChange d: DisplayModeExecutor.Revert(d); break;
            case MouseAccelerationChange m: MouseExecutor.Revert(m); break;
            default: throw Unsupported(change);
        }
    }

    public ChangeState Inspect(Change change) => change switch
    {
        RegistryValueChange r => RegistryExecutor.Inspect(r),
        PowerSchemeChange p => PowerSchemeExecutor.Inspect(p),
        DisplayModeChange d => DisplayModeExecutor.Inspect(d),
        MouseAccelerationChange m => MouseExecutor.Inspect(m),
        _ => throw Unsupported(change),
    };

    public void Backup(Change change, string backupDirectory)
    {
        if (change is RegistryValueChange r)
            RegistryExecutor.Backup(r, backupDirectory);
    }

    private static NotSupportedException Unsupported(Change change) => new($"Desteklenmeyen değişiklik türü: {change.GetType().Name}");
}
