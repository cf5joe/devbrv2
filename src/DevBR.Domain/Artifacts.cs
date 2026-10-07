namespace DevBR.Domain;

public enum ArtifactKind
{
    Inventory,
    EnvironmentVariables,
    Settings,
    Keybindings,
    Snippets,
    Profile,
    ExtensionInventory,
    McpConfiguration,
    Instructions,
    Agents,
    Skills,
    Hooks,
    Plugins,
    Scripts,
    Modules,
    Repository,
    CustomFiles,
    Commands,
    Rules,
    Credentials,
    Assets,
}

public enum Sensitivity
{
    None,
    MayContainSecrets,
    ContainsRecognizedSecrets,
    Credential,
}

/// <summary>Distinguishes content that is backed up from records that are inventory only.</summary>
public enum BackupEligibility
{
    Eligible,
    InventoryOnly,
    ExcludedByDefault,
    Blocked,
}

[Flags]
public enum RestoreCapability
{
    None = 0,
    ReinstallGuidance = 1,
    ExplicitFileRestore = 2,
    StructuredMerge = 4,
    DependencyInstall = 8,
}

public enum LogicalRootKind
{
    UserProfile,
    RoamingAppData,
    LocalAppData,
    ProgramData,
    CustomRoot,
    RepositoryRoot,
}

/// <summary>A path expressed relative to a logical root so it can be remapped on another machine.</summary>
/// <param name="RootKey">Disambiguates multiple custom or repository roots; null for well-known roots.</param>
public sealed record LogicalPath(LogicalRootKind Root, string? RootKey, string RelativePath);

public sealed record MigrationArtifact(
    string Id,
    string OwnerToolId,
    ArtifactKind Kind,
    string DisplayName,
    IReadOnlyList<LogicalPath> Roots,
    Sensitivity Sensitivity,
    BackupEligibility Eligibility,
    RestoreCapability RestoreCapabilities,
    IReadOnlyList<string> Dependencies,
    bool SelectedByDefault,
    string? Description = null,
    IReadOnlyList<string>? ExcludedContent = null,
    string? InventoryItemId = null,
    long? EstimatedBytes = null);

public enum PathMappingOrigin
{
    KnownFolder,
    UserSelected,
    Default,
}

public enum PathMappingStatus
{
    Pending,
    Valid,
    Invalid,
    Conflict,
}

public sealed record PathMapping(
    LogicalPath SourceRoot,
    string SourceAbsolutePath,
    string TargetRoot,
    PathMappingOrigin Origin,
    PathMappingStatus Status);
