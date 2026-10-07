using DevBR.Application.Machine;
using DevBR.Domain;

namespace DevBR.Application.Discovery;

/// <param name="ScanFixedDrives">Traverse every fixed local drive; when false only <paramref name="ExtraRoots"/> are walked.</param>
/// <param name="ExtraRoots">User-added discovery roots, scanned in addition to fixed drives.</param>
/// <param name="ExcludedPaths">Folders never entered, such as DevBR's own scratch and output folders.</param>
/// <param name="IncludeOtherUserProfiles">Off by default; other users' private profile contents are skipped.</param>
/// <param name="OnItemFound">Streams items as they are found so the UI can show live counts.</param>
public sealed record DiscoveryContext(
    Guid JobId,
    IMachine Machine,
    bool ScanFixedDrives,
    IReadOnlyList<string> ExtraRoots,
    IReadOnlyList<string> ExcludedPaths,
    bool IncludeOtherUserProfiles,
    IProgress<OperationEvent>? Progress,
    Action<InventoryItem>? OnItemFound = null);

public sealed record DiscoveryResult(
    IReadOnlyList<InventoryItem> Items,
    IReadOnlyList<DiscoveryCoverage> Coverage)
{
    public IReadOnlyList<MigrationArtifact> Artifacts { get; init; } = [];

    public static DiscoveryResult Empty { get; } = new([], []);
}

/// <summary>
/// A source of inventory observations. Providers must never execute discovered programs, follow
/// junctions automatically, or hydrate cloud placeholders.
/// </summary>
public interface IDiscoveryProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken);
}
