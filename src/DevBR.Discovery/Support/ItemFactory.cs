using DevBR.Application.Machine;
using DevBR.Discovery.Catalog;
using DevBR.Domain;

namespace DevBR.Discovery.Support;

internal static class ItemFactory
{
    /// <summary>An installation of a program, correlated with the known-tool catalog when possible.</summary>
    public static InventoryItem Installation(
        IMachine machine,
        KnownTool? tool,
        string name,
        string? version,
        string? publisher,
        InstallScope scope,
        string? location,
        DiscoveryEvidence evidence,
        Confidence confidence,
        IReadOnlyDictionary<string, string>? properties = null,
        InventoryCategory? category = null)
    {
        var locations = location is null ? Array.Empty<string>() : [Paths.Normalize(location)];
        var verified = location is not null && (machine.FileSystem.DirectoryExists(location) || machine.FileSystem.FileExists(location));
        var resolvedCategory = category ?? tool?.Category ?? InventoryCategory.Application;
        // Product families (.NET SDK vs runtime, JDK vendors) keep their registered names; the catalog
        // name would hide which product and version this is.
        var displayName = tool is { Id: "dotnet" or "java" } ? name : tool?.DisplayName ?? name;

        return new InventoryItem(
            ItemIds.For(resolvedCategory.ToString(), tool?.Id ?? name, location, version, scope.ToString()),
            resolvedCategory,
            displayName,
            string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            string.IsNullOrWhiteSpace(publisher) ? null : publisher.Trim(),
            scope,
            locations,
            [evidence],
            verified && confidence < Confidence.Confirmed ? Confidence.Confirmed : confidence,
            verified ? DetectionStatus.ConfirmedInstalled : DetectionStatus.Detected,
            tool?.AdapterId,
            tool?.Id,
            properties);
    }

    public static InstallScope ScopeForPath(MachineFolders folders, string path)
        => Paths.IsUnder(path, folders.UserProfile) ? InstallScope.User
            : Paths.IsUnder(path, folders.ProgramFiles) || Paths.IsUnder(path, folders.ProgramFilesX86) || Paths.IsUnder(path, folders.ProgramData) || Paths.IsUnder(path, folders.Windows) ? InstallScope.Machine
            : InstallScope.Portable;
}
