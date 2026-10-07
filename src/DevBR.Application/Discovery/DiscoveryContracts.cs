using DevBR.Domain;

namespace DevBR.Application.Discovery;

/// <param name="ExtraRoots">User-added discovery roots, scanned in addition to fixed drives.</param>
/// <param name="IncludeOtherUserProfiles">Off by default; other users' private profile contents are skipped.</param>
public sealed record DiscoveryContext(
    Guid JobId,
    IReadOnlyList<string> ExtraRoots,
    IReadOnlyList<string> ExcludedPaths,
    bool IncludeOtherUserProfiles,
    IProgress<OperationEvent>? Progress);

public sealed record DiscoveryResult(
    IReadOnlyList<InventoryItem> Items,
    IReadOnlyList<DiscoveryCoverage> Coverage);

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
