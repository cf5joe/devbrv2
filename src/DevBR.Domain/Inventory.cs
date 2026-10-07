namespace DevBR.Domain;

public enum InventoryCategory
{
    Application,
    Runtime,
    PackageManager,
    DeveloperTool,
    Extension,
    Plugin,
    AiConfiguration,
    Skill,
    Workflow,
    Repository,
    EnvironmentVariable,
    SystemFact,
    Configuration,
    CustomContent,
}

public enum InstallScope
{
    Unknown,
    User,
    Machine,
    Portable,
    Store,
}

/// <summary>Kept distinct so the UI never presents a heuristic match as a confirmed installation.</summary>
public enum DetectionStatus
{
    Detected,
    ConfirmedInstalled,
}

public enum Confidence
{
    Low,
    Medium,
    High,
    Confirmed,
}

/// <param name="Source">The inventory source, e.g. "registry:HKLM64\Uninstall" or "filesystem".</param>
/// <param name="Detail">What was observed, in a form a user can verify.</param>
public sealed record DiscoveryEvidence(string Source, string Detail, string? Path = null);

public sealed record InventoryItem(
    string Id,
    InventoryCategory Category,
    string Name,
    string? Version,
    string? Publisher,
    InstallScope Scope,
    IReadOnlyList<string> Locations,
    IReadOnlyList<DiscoveryEvidence> Evidence,
    Confidence Confidence,
    DetectionStatus Status,
    string? AdapterId);

public sealed record ExcludedScope(string Path, string Reason);

public sealed record InaccessibleScope(string Path, string Reason);

public sealed record DiscoveryCoverage(
    string Source,
    string? Volume,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long ScannedDirectories,
    long ScannedFiles,
    IReadOnlyList<ExcludedScope> Exclusions,
    IReadOnlyList<InaccessibleScope> Inaccessible,
    IReadOnlyList<string> Errors);
