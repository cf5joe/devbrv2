using System.Collections.Concurrent;
using DevBR.Application.Discovery;
using DevBR.Application.Machine;
using DevBR.Discovery.Adapters;
using DevBR.Discovery.Providers;
using DevBR.Discovery.Support;
using DevBR.Domain;
using Microsoft.Extensions.Logging;

namespace DevBR.Discovery;

public sealed record DiscoveryOptions(
    bool ScanFixedDrives,
    IReadOnlyList<string> ExtraRoots,
    IReadOnlyList<string> ExcludedPaths,
    bool IncludeOtherUserProfiles = false)
{
    public static DiscoveryOptions Default { get; } = new(true, [], []);
}

/// <param name="FilesScanned">Open-ended while scanning; shown with indeterminate progress, never as a percentage.</param>
public sealed record DiscoveryProgress(
    string Stage,
    string? CurrentScope,
    long FilesScanned,
    IReadOnlyDictionary<InventoryCategory, int> ByCategory,
    IReadOnlyDictionary<string, int> ByDrive,
    int ProvidersCompleted,
    int ProvidersTotal,
    TimeSpan Elapsed);

public sealed record DiscoverySnapshot(
    Guid RunId,
    string MachineKey,
    MachineInfo Machine,
    bool Simulated,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Cancelled,
    DiscoveryOptions Options,
    IReadOnlyList<InventoryItem> Items,
    IReadOnlyList<MigrationArtifact> Artifacts,
    IReadOnlyList<DiscoveryCoverage> Coverage);

/// <summary>
/// Runs every provider, merges duplicate observations of the same installation, links project-level
/// configuration to repositories, and turns the inventory into selectable backup artifacts with the
/// confirmed defaults (inventory and non-secret machine environment only).
/// </summary>
public sealed class DiscoveryEngine(IReadOnlyList<IDiscoveryProvider> providers, ILogger<DiscoveryEngine> logger)
{
    public const string InventoryArtifactId = "inventory:machine";

    public static IReadOnlyList<IToolAdapter> DefaultAdapters { get; } =
    [
        new EnvironmentAdapter(),
        new VsCodeAdapter(insiders: false),
        new VsCodeAdapter(insiders: true),
        new CursorAdapter(),
        new CopilotAdapter(),
        new CodexAdapter(),
        new ClaudeCodeAdapter(),
        new ClaudeDesktopAdapter(),
        new GeminiCliAdapter(),
        new GitAdapter(),
        new GitHubCliAdapter(),
        new PowerShellAdapter(),
        new WindowsTerminalAdapter(),
        new WslAdapter(),
        new DockerAdapter(),
    ];

    /// <summary>Providers in priority order: earlier providers' names and ids win when observations merge.</summary>
    public static IReadOnlyList<IDiscoveryProvider> DefaultProviders(int fileSystemParallelism = 4) =>
    [
        new UninstallRegistryProvider(),
        new StorePackageProvider(),
        new AppPathsProvider(),
        new StartMenuProvider(),
        new PathProvider(),
        new PackageManagerProvider(),
        new EnvironmentProvider(),
        new SystemFactsProvider(),
        new AdapterDiscoveryProvider(DefaultAdapters),
        new FileSystemScanProvider(fileSystemParallelism),
    ];

    public static string MachineKey(IMachine machine)
        => machine is { IsSimulated: true } ? $"sim:{machine.Info.ComputerName}:{machine.Info.UserName}" : $"local:{machine.Info.ComputerName}:{machine.Info.UserName}";

    public async Task<DiscoverySnapshot> RunAsync(IMachine machine, DiscoveryOptions options, IProgress<DiscoveryProgress>? progress, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var byCategory = new ConcurrentDictionary<InventoryCategory, int>();
        var byDrive = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var streamed = new ConcurrentDictionary<string, InventoryItem>(StringComparer.Ordinal);
        var results = new DiscoveryResult?[providers.Count];
        string? scope = null;
        long filesScanned = 0;
        var completed = 0;

        var context = new DiscoveryContext(
            runId, machine, options.ScanFixedDrives, options.ExtraRoots, options.ExcludedPaths, options.IncludeOtherUserProfiles,
            new SynchronousProgress<OperationEvent>(e =>
            {
                Volatile.Write(ref scope, e.Message);
                Interlocked.Exchange(ref filesScanned, Math.Max(Interlocked.Read(ref filesScanned), e.FilesProcessed));
            }),
            item =>
            {
                if (streamed.TryAdd(item.Id, item))
                {
                    byCategory.AddOrUpdate(item.Category, 1, (_, n) => n + 1);
                    var drive = item.Locations.Count > 0 ? Paths.Root(item.Locations[0]) ?? "Other" : "System";
                    byDrive.AddOrUpdate(drive, 1, (_, n) => n + 1);
                }
            });

        DiscoveryProgress Snapshot(string stage) => new(stage, Volatile.Read(ref scope), Interlocked.Read(ref filesScanned),
            new Dictionary<InventoryCategory, int>(byCategory), new Dictionary<string, int>(byDrive, StringComparer.OrdinalIgnoreCase),
            Volatile.Read(ref completed), providers.Count, clock.Elapsed);

        using var reporting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var reporter = progress is null ? Task.CompletedTask : Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            try
            {
                while (await timer.WaitForNextTickAsync(reporting.Token).ConfigureAwait(false))
                {
                    progress.Report(Snapshot("Scanning"));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        var cancelled = false;
        try
        {
            await Task.WhenAll(providers.Select((provider, index) => Task.Run(async () =>
            {
                try
                {
                    results[index] = await provider.DiscoverAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Discovery provider {Provider} failed.", provider.Id);
                    var failed = new CoverageBuilder(provider.DisplayName);
                    failed.Error($"{provider.DisplayName} failed: {ex.Message}");
                    results[index] = new DiscoveryResult([], [failed.Build()]);
                }
                finally
                {
                    Interlocked.Increment(ref completed);
                }
            }, CancellationToken.None))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        finally
        {
            await reporting.CancelAsync().ConfigureAwait(false);
            await reporter.ConfigureAwait(false);
        }

        progress?.Report(Snapshot(cancelled ? "Cancelled" : "Finishing"));

        // Completed providers in priority order, then anything streamed by providers that were cancelled.
        var raw = results.OfType<DiscoveryResult>().SelectMany(r => r.Items).ToList();
        var known = new HashSet<string>(raw.Select(i => i.Id), StringComparer.Ordinal);
        raw.AddRange(streamed.Values.Where(i => !known.Contains(i.Id)));

        var items = Associate(Merge(raw));
        var artifacts = BuildArtifacts(machine, items, results.OfType<DiscoveryResult>().SelectMany(r => r.Artifacts));
        var coverage = results.OfType<DiscoveryResult>().SelectMany(r => r.Coverage).ToList();
        if (cancelled)
        {
            var note = new CoverageBuilder("Discovery");
            note.Error("Discovery was cancelled. Results are incomplete.");
            coverage.Add(note.Build());
        }

        logger.LogInformation("Discovery {RunId} finished: {Items} items, {Artifacts} artifacts, cancelled={Cancelled}.", runId, items.Count, artifacts.Count, cancelled);
        return new DiscoverySnapshot(runId, MachineKey(machine), machine.Info, machine.IsSimulated, started, DateTimeOffset.UtcNow, cancelled, options, items, artifacts, coverage);
    }

    /// <summary>
    /// Merges observations of the same installation (e.g. an uninstall record, a PATH hit and a Start menu
    /// shortcut for one Git install) without collapsing distinct installations or versions.
    /// </summary>
    public static IReadOnlyList<InventoryItem> Merge(IEnumerable<InventoryItem> observations)
    {
        var result = new List<InventoryItem>();
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var clusters = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in observations)
        {
            if (byId.TryGetValue(item.Id, out var existing))
            {
                result[existing] = Combine(result[existing], item);
                continue;
            }

            if (IsInstallation(item) && item.ToolId is { } toolId)
            {
                var candidates = clusters.TryGetValue(toolId, out var list) ? list : clusters[toolId] = [];
                var match = candidates.FirstOrDefault(i => SameInstallation(result[i], item), -1);
                if (match >= 0)
                {
                    result[match] = Combine(result[match], item);
                    byId[item.Id] = match;
                    continue;
                }

                candidates.Add(result.Count);
            }

            byId[item.Id] = result.Count;
            result.Add(item);
        }

        return result;
    }

    private static bool IsInstallation(InventoryItem item)
        => item.Category is InventoryCategory.Application or InventoryCategory.DeveloperTool or InventoryCategory.Runtime or InventoryCategory.PackageManager;

    private static bool SameInstallation(InventoryItem a, InventoryItem b)
    {
        if (a.Locations.Count == 0 || b.Locations.Count == 0)
        {
            // Without a location, only the same version (7.6.6 == 7.6.6.0) can be the same installation.
            return a.Version is not null && b.Version is not null
                && string.Equals(NormalizeVersion(a.Version), NormalizeVersion(b.Version), StringComparison.OrdinalIgnoreCase)
                && (a.Locations.Count == 0) != (b.Locations.Count == 0);
        }

        return a.Locations.Any(x => b.Locations.Any(y => Paths.IsUnder(x, y) || Paths.IsUnder(y, x)));
    }

    public static string NormalizeVersion(string version)
    {
        var core = version.Trim().TrimStart('v', 'V').Split(['+', ' ', '-'], 2)[0];
        var parts = core.Split('.').ToList();
        while (parts.Count > 2 && parts[^1] == "0")
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return string.Join('.', parts);
    }

    private static InventoryItem Combine(InventoryItem first, InventoryItem second)
    {
        var properties = new Dictionary<string, string>(second.Properties ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (key, value) in first.Properties ?? new Dictionary<string, string>())
        {
            properties[key] = value;
        }

        // Prefer the version read from a verified installation.
        var version = first.Status == DetectionStatus.ConfirmedInstalled || second.Version is null ? first.Version ?? second.Version : second.Version;
        var scope = first.Scope is InstallScope.Unknown ? second.Scope
            : second.Scope is InstallScope.Portable && properties.ContainsKey("portableMode") ? InstallScope.Portable
            : first.Scope;

        return first with
        {
            Version = version,
            Publisher = first.Publisher ?? second.Publisher,
            Scope = scope,
            Locations = [.. first.Locations.Concat(second.Locations).Distinct(StringComparer.OrdinalIgnoreCase)],
            Evidence = [.. first.Evidence.Concat(second.Evidence).Distinct()],
            Confidence = (Confidence)Math.Max((int)first.Confidence, (int)second.Confidence),
            Status = first.Status == DetectionStatus.ConfirmedInstalled || second.Status == DetectionStatus.ConfirmedInstalled ? DetectionStatus.ConfirmedInstalled : DetectionStatus.Detected,
            AdapterId = first.AdapterId ?? second.AdapterId,
            ToolId = first.ToolId ?? second.ToolId,
            Properties = properties.Count == 0 ? null : properties,
        };
    }

    /// <summary>
    /// Links project-level configuration to the repository that contains it, and drops "project" files
    /// that are really inside a tool's user-level configuration folder (e.g. a CODEX_HOME override).
    /// </summary>
    private static IReadOnlyList<InventoryItem> Associate(IReadOnlyList<InventoryItem> items)
    {
        var toolRoots = items.Where(i => i.Category == InventoryCategory.Configuration && i.Properties?.ContainsKey("managedPolicy") != true)
            .SelectMany(i => i.Locations).ToList();
        items = [.. items.Where(i => i.Category != InventoryCategory.AiConfiguration
            || i.Properties?.GetValueOrDefault("level") != "project"
            || !toolRoots.Any(root => Paths.IsUnder(i.Locations[0], root)))];

        var repositories = items.Where(i => i.Category == InventoryCategory.Repository && i.Locations.Count > 0)
            .OrderByDescending(r => r.Locations[0].Length).ToList();
        var configCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        var associated = items.Select(item =>
        {
            if (item.Category != InventoryCategory.AiConfiguration || item.Properties?.GetValueOrDefault("project") is not { } project)
            {
                return item;
            }

            var repository = repositories.FirstOrDefault(r => Paths.IsUnder(project, r.Locations[0]));
            if (repository is null)
            {
                return item;
            }

            configCounts[repository.Id] = configCounts.GetValueOrDefault(repository.Id) + 1;
            var properties = new Dictionary<string, string>(item.Properties) { ["repository"] = repository.Locations[0] };
            return item with { Properties = properties };
        }).ToList();

        return [.. associated.Select(item => configCounts.TryGetValue(item.Id, out var count)
            ? item with { Properties = new Dictionary<string, string>(item.Properties ?? new Dictionary<string, string>()) { ["projectConfigs"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
            : item)];
    }

    private static IReadOnlyList<MigrationArtifact> BuildArtifacts(IMachine machine, IReadOnlyList<InventoryItem> items, IEnumerable<MigrationArtifact> adapterArtifacts)
    {
        var artifacts = new List<MigrationArtifact>
        {
            new(InventoryArtifactId, "devbr", ArtifactKind.Inventory, "Application and system inventory", [], Sensitivity.None,
                BackupEligibility.Eligible, RestoreCapability.ReinstallGuidance, [], SelectedByDefault: true,
                $"{items.Count(i => i.Category != InventoryCategory.EnvironmentVariable)} inventory records: installed software, versions and reinstall guidance. Program files are never copied."),
        };

        artifacts.AddRange(adapterArtifacts);

        var repositories = items.Where(i => i.Category == InventoryCategory.Repository).ToList();
        var repoArtifactIds = repositories.ToDictionary(r => r.Locations[0], r => $"repository:{r.Id}", StringComparer.OrdinalIgnoreCase);
        foreach (var repository in repositories)
        {
            var p = repository.Properties ?? new Dictionary<string, string>();
            var notes = new List<string>();
            if (p.GetValueOrDefault("head") is { } head) { notes.Add($"on {head}"); }
            if (p.ContainsKey("locked")) { notes.Add("a Git operation was in progress when scanned"); }
            if (p.ContainsKey("objectAlternates")) { notes.Add("uses external object storage"); }
            if (p.ContainsKey("lfs")) { notes.Add("uses Git LFS"); }
            if (p.ContainsKey("credentialsInRemoteUrl")) { notes.Add("a remote URL contains credentials"); }

            // Linked worktrees and submodules depend on the repository that owns their git directory.
            var dependencies = new List<string>();
            if (p.GetValueOrDefault("gitDir") is { } gitDir)
            {
                var owner = repoArtifactIds.Keys.Where(k => !Paths.Equal(k, repository.Locations[0]) && Paths.IsUnder(gitDir, k)).OrderByDescending(k => k.Length).FirstOrDefault();
                if (owner is not null)
                {
                    dependencies.Add(repoArtifactIds[owner]);
                }
            }

            var blocked = p.ContainsKey("gitDirMissing");
            artifacts.Add(new MigrationArtifact(
                $"repository:{repository.Id}",
                "git",
                ArtifactKind.Repository,
                $"{repository.Name} ({p.GetValueOrDefault("kind", "repository")})",
                [new LogicalPath(LogicalRootKind.RepositoryRoot, repository.Locations[0], string.Empty)],
                p.ContainsKey("credentialsInRemoteUrl") ? Sensitivity.ContainsRecognizedSecrets : Sensitivity.MayContainSecrets,
                blocked ? BackupEligibility.Blocked : BackupEligibility.Eligible,
                RestoreCapability.ExplicitFileRestore,
                dependencies,
                SelectedByDefault: false,
                $"{repository.Locations[0]}{(notes.Count == 0 ? string.Empty : " — " + string.Join("; ", notes))}." +
                (blocked ? " Its git directory is missing, so a complete backup is not possible." : string.Empty),
                ["Regenerable folders such as node_modules, bin and obj (editable before backup)"],
                repository.Id));
        }

        // Project configuration outside any repository is offered on its own.
        foreach (var config in items.Where(i => i.Category == InventoryCategory.AiConfiguration && i.Properties?.ContainsKey("repository") != true && i.Properties?.ContainsKey("project") == true))
        {
            artifacts.Add(new MigrationArtifact(
                $"project-config:{config.Id}", config.ToolId ?? "unknown", ArtifactKind.CustomFiles, $"{config.Name} — {config.Properties!["project"]}",
                [Paths.ToLogical(machine.Folders, config.Locations[0])], Sensitivity.MayContainSecrets, BackupEligibility.Eligible,
                RestoreCapability.ExplicitFileRestore, [], false, "Project-level configuration outside a repository.", null, config.Id));
        }

        return [.. artifacts.GroupBy(a => a.Id, StringComparer.Ordinal).Select(g => g.First())];
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

/// <summary>Runs every tool adapter's discovery as one provider.</summary>
public sealed class AdapterDiscoveryProvider(IReadOnlyList<IToolAdapter> adapters) : IDiscoveryProvider
{
    public string Id => "adapters";

    public string DisplayName => "Developer tool configuration";

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryContext context, CancellationToken cancellationToken)
    {
        var items = new List<InventoryItem>();
        var artifacts = new List<MigrationArtifact>();
        var coverage = new CoverageBuilder($"{DisplayName} ({adapters.Count} tools checked)");

        foreach (var adapter in adapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = adapter.Discover(context.Machine, cancellationToken);
                items.AddRange(result.Items);
                artifacts.AddRange(result.Artifacts);
                coverage.Absorb(result.Coverage);
                foreach (var item in result.Items)
                {
                    context.OnItemFound?.Invoke(item);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                coverage.Error($"{adapter.DisplayName} could not be inspected: {ex.Message}");
            }
        }

        coverage.CountFiles(artifacts.Count);
        return Task.FromResult(new DiscoveryResult(items, [coverage.Build()]) { Artifacts = artifacts });
    }
}
