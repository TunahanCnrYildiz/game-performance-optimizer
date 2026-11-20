namespace Optimizer.Core.Diagnostics;

/// <summary>Ordered from "nothing to do" to "fix this first".</summary>
public enum Severity
{
    Ok = 0,
    Info = 1,
    Suggestion = 2,
    Warning = 3,
}

/// <summary>Rough expected effect on game performance if the finding is addressed.</summary>
public enum Impact
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum Category
{
    Display,
    Graphics,
    Memory,
    Power,
    Windows,
    Security,
    Storage,
    Background,
    System,
}

public sealed record Finding
{
    /// <summary>Stable id, e.g. "display.refresh-rate". Later used to link a finding to a tweak.</summary>
    public required string Id { get; init; }

    public required Category Category { get; init; }
    public required Severity Severity { get; init; }
    public Impact Impact { get; init; } = Impact.None;
    public required string Title { get; init; }
    public string? Detail { get; init; }

    /// <summary>What the user can do about it. Phase 1 never applies anything itself.</summary>
    public string? Recommendation { get; init; }

    /// <summary>Trade-offs the user must know before acting (security, battery, etc.).</summary>
    public string? Caveat { get; init; }
}
