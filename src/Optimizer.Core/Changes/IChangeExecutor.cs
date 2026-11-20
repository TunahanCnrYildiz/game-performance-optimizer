namespace Optimizer.Core.Changes;

public enum ChangeState
{
    /// <summary>The system currently matches the change's target.</summary>
    Applied,

    /// <summary>The system currently matches the captured original.</summary>
    Original,

    /// <summary>Something else changed it since (the user, Windows Update, a driver).</summary>
    Diverged,
}

/// <summary>Platform-specific code that actually touches the system.</summary>
public interface IChangeExecutor
{
    /// <summary>Returns <paramref name="desired"/> with its original state recorded and <see cref="Change.Captured"/> set.</summary>
    Change Capture(Change desired);

    void Apply(Change change);

    /// <summary>Restores the captured original state.</summary>
    void Revert(Change change);

    ChangeState Inspect(Change change);

    /// <summary>Optional extra safety copy (e.g. a .reg export) written before the change is applied.</summary>
    void Backup(Change change, string backupDirectory)
    {
    }
}
