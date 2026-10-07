using DevBR.Domain;

namespace DevBR.Application.Migration;

/// <param name="SupportedVersions">Version ranges with verified semantic handling. Unknown versions fall back to inventory or explicit file restore.</param>
public sealed record AdapterDescriptor(
    string Id,
    string DisplayName,
    IReadOnlyList<string> SupportedVersions,
    RestoreCapability Capabilities);

public sealed record AdapterContext(
    Guid JobId,
    IReadOnlyList<InventoryItem> Inventory,
    IProgress<OperationEvent>? Progress);

public sealed record ArtifactSelection(
    IReadOnlyList<MigrationArtifact> Artifacts,
    IReadOnlySet<string> IncludedSensitiveItemIds);

/// <param name="StagingRoot">Private, access-restricted staging directory for this artifact's captured bytes.</param>
public sealed record CaptureContext(
    Guid JobId,
    string StagingRoot,
    IProgress<OperationEvent>? Progress);

public sealed record CaptureResult(
    IReadOnlyList<ArchiveEntry> Entries,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> OmittedItems);

public sealed record RestoreTarget(
    string UserProfilePath,
    string RoamingAppDataPath,
    string LocalAppDataPath,
    IReadOnlyList<PathMapping> Mappings,
    string ExtractedPayloadRoot);

public sealed record ValidationOutcome(VerificationLevel Level, IReadOnlyList<string> Details);

/// <summary>
/// Tool-specific detection, capture, preflight, planning and validation. Adapters produce declarative
/// <see cref="RestoreOperation"/>s; they never write to the target directly — the executor does.
/// </summary>
public interface IMigrationAdapter
{
    AdapterDescriptor Descriptor { get; }

    Task<IReadOnlyList<MigrationArtifact>> DescribeArtifactsAsync(AdapterContext context, CancellationToken cancellationToken);

    Task<CaptureResult> CaptureAsync(ArtifactSelection selection, CaptureContext context, CancellationToken cancellationToken);

    Task<IReadOnlyList<PreflightFinding>> PreflightAsync(ArtifactSelection selection, RestoreTarget target, CancellationToken cancellationToken);

    Task<IReadOnlyList<RestoreOperation>> PlanRestoreAsync(ArtifactSelection selection, RestoreTarget target, CancellationToken cancellationToken);

    Task<ValidationOutcome> ValidateAsync(RestoreResult result, CancellationToken cancellationToken);
}
