using System.Globalization;
using DevBR.Application.Machine;
using DevBR.Discovery.Catalog;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Discovery.Adapters;

public sealed record AdapterDiscovery(IReadOnlyList<InventoryItem> Items, IReadOnlyList<MigrationArtifact> Artifacts, DiscoveryCoverage Coverage);

/// <summary>
/// The discovery half of a migration adapter: where a tool keeps its user-level configuration, what is
/// eligible for backup, and what is deliberately left out (sessions, caches, credentials). Locations
/// honor documented environment overrides before default paths.
/// </summary>
public interface IToolAdapter
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Verified versions, locations, capabilities and prerequisites. Other versions fall back to whole-file restore or inventory.</summary>
    AdapterSupport Support { get; }

    AdapterDiscovery Discover(IMachine machine, CancellationToken cancellationToken);
}

public abstract class ToolAdapter : IToolAdapter
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    public abstract AdapterSupport Support { get; }

    /// <summary>The known-tool id this adapter's artifacts belong to.</summary>
    protected abstract string ToolId { get; }

    /// <summary>A support descriptor whose host is this adapter's tool.</summary>
    protected AdapterSupport Declare(string hostName, VersionRange[] versions, string[] locations, AdapterCapabilities capabilities, params string[] prerequisites)
        => new(ToolId, hostName, versions, locations, capabilities, prerequisites);

    public AdapterDiscovery Discover(IMachine machine, CancellationToken cancellationToken)
    {
        var scope = new AdapterScope(machine, Id, ToolId, DisplayName);
        Discover(scope, cancellationToken);
        return scope.Build();
    }

    protected abstract void Discover(AdapterScope scope, CancellationToken cancellationToken);
}

/// <summary>Collects one adapter's findings with consistent ids, logical paths and sensitivity handling.</summary>
public sealed class AdapterScope(IMachine machine, string adapterId, string toolId, string adapterName)
{
    private const int MaxSizedEntries = 20_000;

    private readonly List<InventoryItem> _items = [];
    private readonly List<MigrationArtifact> _artifacts = [];
    private readonly CoverageBuilder _coverage = new($"Adapter: {adapterName}");

    public IMachine Machine { get; } = machine;

    public MachineFolders Folders => Machine.Folders;

    public IMachineFileSystem Fs => Machine.FileSystem;

    public string ToolId { get; } = toolId;

    public CoverageBuilder Coverage => _coverage;

    /// <summary>The first configured override, else the default location. Records which one was used.</summary>
    public (string Path, string? Override) ResolveRoot(string defaultPath, params string[] overrideVariables)
    {
        foreach (var variable in overrideVariables)
        {
            if (Machine.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                return (Paths.Normalize(value), variable);
            }
        }

        return (Paths.Normalize(defaultPath), null);
    }

    public bool Exists(string path) => Fs.FileExists(path) || Fs.DirectoryExists(path);

    public string Combine(string root, params string[] parts) => Paths.Combine(root, parts);

    /// <summary>
    /// Adds an artifact for the paths that exist. Sensitivity is raised automatically when small text
    /// files contain recognizable credentials.
    /// </summary>
    public MigrationArtifact? Artifact(
        string key,
        ArtifactKind kind,
        string displayName,
        IEnumerable<string> paths,
        string description,
        Sensitivity sensitivity = Sensitivity.None,
        BackupEligibility eligibility = BackupEligibility.Eligible,
        RestoreCapability capability = RestoreCapability.ExplicitFileRestore,
        IReadOnlyList<string>? excluded = null,
        bool selectedByDefault = false,
        string? customRootKey = null)
    {
        var existing = paths.Where(Exists).Select(Paths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (existing.Count == 0)
        {
            return null;
        }

        // An empty folder has nothing to back up.
        var bytes = existing.Sum(Size);
        if (bytes == 0 && existing.All(Fs.DirectoryExists) && existing.All(p => Children(p).Count == 0))
        {
            return null;
        }

        if (sensitivity < Sensitivity.ContainsRecognizedSecrets && existing.Any(ContainsRecognizedSecret))
        {
            sensitivity = Sensitivity.ContainsRecognizedSecrets;
        }

        var artifact = new MigrationArtifact(
            $"{adapterId}:{key}",
            ToolId,
            kind,
            displayName,
            [.. existing.Select(p => Paths.ToLogical(Folders, p, customRootKey))],
            sensitivity,
            eligibility,
            capability,
            [],
            selectedByDefault,
            description,
            excluded,
            null,
            bytes);
        _artifacts.Add(artifact);
        return artifact;
    }

    public MigrationArtifact? Artifact(string key, ArtifactKind kind, string displayName, string path, string description,
        Sensitivity sensitivity = Sensitivity.None, BackupEligibility eligibility = BackupEligibility.Eligible,
        RestoreCapability capability = RestoreCapability.ExplicitFileRestore, IReadOnlyList<string>? excluded = null, string? customRootKey = null)
        => Artifact(key, kind, displayName, [path], description, sensitivity, eligibility, capability, excluded, false, customRootKey);

    /// <summary>A credential file: listed so the user knows it exists, excluded unless explicitly included (which forces encryption).</summary>
    public MigrationArtifact? Credential(string key, string displayName, string path, string description, bool portable = true, string? customRootKey = null)
        => Artifact(key, ArtifactKind.Credentials, displayName, [path], description, Sensitivity.Credential,
            portable ? BackupEligibility.ExcludedByDefault : BackupEligibility.Blocked, RestoreCapability.ExplicitFileRestore, null, false, customRootKey);

    public void AddArtifact(MigrationArtifact artifact) => _artifacts.Add(artifact);

    /// <summary>A record that the tool's configuration is present on this machine.</summary>
    public InventoryItem ConfigurationItem(string name, string location, IReadOnlyDictionary<string, string>? properties = null, InventoryCategory category = InventoryCategory.Configuration)
    {
        var item = new InventoryItem(
            ItemIds.For(category.ToString(), $"{adapterId}-{name}", location),
            category,
            name,
            null,
            null,
            ItemFactory.ScopeForPath(Folders, location) == InstallScope.Machine ? InstallScope.Machine : InstallScope.User,
            [Paths.Normalize(location)],
            [new DiscoveryEvidence($"adapter:{adapterId}", "Configuration found at its documented location", location)],
            Confidence.Confirmed,
            DetectionStatus.Detected,
            adapterId,
            ToolId,
            properties);
        _items.Add(item);
        return item;
    }

    public void Item(InventoryItem item) => _items.Add(item);

    public IReadOnlyList<FileSystemEntry> Children(string directory)
    {
        try
        {
            return [.. Fs.EnumerateEntries(directory)];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            _coverage.Inaccessible(directory, "Access denied.");
            return [];
        }
    }

    public AdapterDiscovery Build() => new(_items, _artifacts, _coverage.Build());

    public static string Count(int count, string singular, string plural)
        => $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    private bool ContainsRecognizedSecret(string path)
    {
        if (!Fs.FileExists(path))
        {
            return false;
        }

        var text = Fs.ReadText(path, 1024 * 1024);
        return text is not null && SecretDetector.LooksLikeSecretValue(text);
    }

    private long Size(string path)
    {
        if (Fs.GetEntry(path) is not { } entry)
        {
            return 0;
        }

        if (!entry.IsDirectory)
        {
            return entry.Length;
        }

        long total = 0;
        var visited = 0;
        var stack = new Stack<string>([path]);
        while (stack.Count > 0 && visited < MaxSizedEntries)
        {
            foreach (var child in Children(stack.Pop()))
            {
                visited++;
                if (child.IsReparsePoint || child.IsCloudPlaceholder)
                {
                    continue;
                }

                if (child.IsDirectory)
                {
                    stack.Push(child.FullPath);
                }
                else
                {
                    total += child.Length;
                }
            }
        }

        return total;
    }

    internal static KnownTool? Tool(string id) => KnownTools.Get(id);
}
