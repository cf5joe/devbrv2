using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBR.Application.Archive;
using DevBR.Application.Machine;
using DevBR.Application.Restore;
using DevBR.Backup;
using DevBR.Discovery;
using DevBR.Discovery.Adapters;
using DevBR.Discovery.Catalog;
using DevBR.Discovery.Providers;
using DevBR.Discovery.Support;
using DevBR.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Restore;

/// <summary>
/// Mandatory preflight and restore planning. Reads the backup's indexes and small configuration files,
/// inspects the target computer read-only (the machine abstraction has no write operations), and
/// produces findings with next steps plus a declarative plan. Nothing on the target is changed.
/// </summary>
public sealed class RestorePlanner(IArchiveService archive, ILogger<RestorePlanner> logger)
{
    private const long MaxComparedBytes = 8L * 1024 * 1024;
    private const long Fat32MaxFile = (4L * 1024 * 1024 * 1024) - 1;

    /// <summary>Variables Windows defines itself; restore never proposes changing them.</summary>
    private static readonly HashSet<string> SystemDefinedVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ComSpec", "DriverData", "NUMBER_OF_PROCESSORS", "OS", "PATHEXT", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "PSModulePath", "TEMP", "TMP", "USERNAME", "windir", "OneDrive",
        "OneDriveConsumer", "OneDriveCommercial", "ChocolateyLastPathUpdate",
    };

    /// <summary>Tools that need the user to sign in again after a restore (credentials are not carried over by default).</summary>
    private static readonly Dictionary<string, string> Reauthentication = new(StringComparer.Ordinal)
    {
        ["gh"] = "GitHub CLI: run 'gh auth login'.",
        ["claude-code"] = "Claude Code: start 'claude' and sign in.",
        ["claude-desktop"] = "Claude Desktop: sign in when it starts.",
        ["codex"] = "Codex: run 'codex login'.",
        ["copilot-cli"] = "GitHub Copilot CLI: run 'copilot' and use /login.",
        ["gemini-cli"] = "Gemini CLI: start 'gemini' and sign in.",
        ["docker-desktop"] = "Docker: run 'docker login' for each private registry.",
        ["git"] = "Git: your credential manager will ask you to sign in on the first push or pull.",
        ["vscode"] = "VS Code: turn Settings Sync and GitHub accounts back on if you use them.",
        ["cursor"] = "Cursor: sign in when it starts.",
    };

    public async Task<RestorePreflight> PreflightAsync(RestoreRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var target = request.Target;
        var overview = request.Overview;
        var context = new PlanContext(request);

        // --- What is on the target? (targeted discovery: no drive walk) ------------------------------
        progress?.Report("Checking this computer");
        var providers = DiscoveryEngine.DefaultProviders().Where(p => p is not FileSystemScanProvider).ToList();
        var targetSnapshot = await new DiscoveryEngine(providers, NullLogger<DiscoveryEngine>.Instance)
            .RunAsync(target, DiscoveryOptions.Default with { ScanFixedDrives = false }, null, cancellationToken).ConfigureAwait(false);
        context.Installed = targetSnapshot.Items
            .Where(i => i.ToolId is not null && i.Category is InventoryCategory.DeveloperTool or InventoryCategory.Runtime or InventoryCategory.PackageManager or InventoryCategory.Application)
            .GroupBy(i => i.ToolId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        context.TargetExtensions = targetSnapshot.Items.Where(i => i.Category == InventoryCategory.Extension)
            .Select(i => $"{i.ToolId}:{i.Name}".ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        context.SourceInventory = ReadInventory(overview);

        // --- Mappings ---------------------------------------------------------------------------------
        var selected = overview.Artifacts.Where(a => request.SelectedKeys.Contains(a.Record.Key)).ToList();
        var mapper = BuildMapper(request, selected, context);
        context.Mapper = mapper;
        context.Rewriter = new PathRewriter(mapper);

        // --- Small configuration files, extracted privately for comparison and preview --------------
        progress?.Report("Reading configuration from the backup");
        await ExtractComparablesAsync(request, selected, context, cancellationToken).ConfigureAwait(false);

        // --- Per artifact ---------------------------------------------------------------------------------
        foreach (var summary in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Planning {summary.Record.Artifact.DisplayName}");
            var record = summary.Record;

            if (record.Status == "Blocked")
            {
                context.Finding(FindingSeverity.Information, [record.Artifact.Id], null,
                    $"{record.Artifact.DisplayName} was not captured in this backup.", "It was blocked when the backup was made.",
                    ["Back it up again from the source computer if you need it."], canRecheck: false);
                continue;
            }

            var host = HostPrerequisite(record.Artifact, context);
            switch (record.Kind)
            {
                case CaptureKind.InventoryOnly:
                    PlanInventory(record, context);
                    break;
                case CaptureKind.Environment:
                    PlanEnvironment(record, context);
                    break;
                case CaptureKind.Files when record.Artifact.Kind == ArtifactKind.Repository:
                    PlanRepository(summary, context);
                    break;
                case CaptureKind.Files when record.Artifact.Kind == ArtifactKind.ExtensionInventory:
                    PlanExtensions(record, host, context);
                    break;
                default:
                    PlanFiles(summary, host, context);
                    break;
            }
        }

        // --- Cross-cutting checks ---------------------------------------------------------------------
        PlanMcpRuntimes(context);
        CheckRunningApplications(context);
        CheckPrivilege(context);
        CheckCredentials(selected, context);
        CheckPolicies(context);
        CheckCapacityAndFilesystem(context);

        var operations = context.Operations;
        var effects = operations.Where(o => o.Enabled && o.Operation.Action is not (RestoreAction.Skip or RestoreAction.ManualStep or RestoreAction.Validate))
            .Select(PlanApproval.EffectKey);
        var plan = new RestorePlan(Guid.NewGuid(), overview.Manifest.ArchiveId, [.. operations.Select(o => o.Operation)], [.. context.Findings], PlanApproval.Hash(effects));

        logger.LogInformation("Preflight for backup {ArchiveId}: {Operations} operations, {Findings} findings.", overview.Manifest.ArchiveId, operations.Count, context.Findings.Count());
        return new RestorePreflight(plan, operations, context.MappingRows, context.Rewrites, context.Mcp, context.Reinstall, context.Blocked, mapper);
    }

    // --- Mapping ------------------------------------------------------------------------------------------

    private static PathMapper BuildMapper(RestoreRequest request, List<ArtifactSummary> selected, PlanContext context)
    {
        var target = request.Target;
        var drives = target.FileSystem.GetFixedDrives().Select(d => Paths.Normalize(d.Root)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var source = SourceFolders.From(request.Overview.Artifacts.Select(a => a.Record));
        var byPrefix = new Dictionary<string, RootMapping>(StringComparer.OrdinalIgnoreCase);

        // Lowest precedence: a path on a drive this computer also has stays where it is. Paths on
        // drives this computer lacks have no mapping and are reported as unresolved.
        foreach (var drive in drives)
        {
            byPrefix[drive] = new RootMapping(drive, drive, PathMappingOrigin.Default);
        }

        foreach (var mapping in PathMapper.KnownFolders(source, target.Folders))
        {
            byPrefix[Paths.Normalize(mapping.SourcePrefix)] = mapping;
        }

        // Custom and repository roots keep their location when that drive exists on this computer.
        foreach (var root in selected.SelectMany(a => a.Record.Roots).Where(r => r.Logical.Root is LogicalRootKind.CustomRoot or LogicalRootKind.RepositoryRoot))
        {
            var key = Paths.Normalize(root.Logical.RootKey ?? root.SourcePath);
            if (!byPrefix.ContainsKey(key) && Paths.Root(key) is { } drive && drives.Contains(drive))
            {
                byPrefix[key] = new RootMapping(key, key, PathMappingOrigin.Default);
            }
        }

        foreach (var mapping in request.UserMappings)
        {
            byPrefix[Paths.Normalize(mapping.SourcePrefix)] = mapping with { Origin = PathMappingOrigin.UserSelected };
        }

        var mapper = new PathMapper(byPrefix.Values);

        // One row per selected root, for the mapping table.
        foreach (var root in selected.SelectMany(a => a.Record.Roots.Select(r => (Artifact: a.Record.Artifact, Root: r))))
        {
            var mapping = mapper.MappingFor(root.Root.SourcePath);
            var mapped = mapper.Map(root.Root.SourcePath);
            var status = mapped is null ? PathMappingStatus.Invalid
                : Paths.Root(mapped) is { } d && !drives.Contains(d) ? PathMappingStatus.Invalid
                : PathMappingStatus.Valid;
            context.MappingRows.Add(new PathMapping(root.Root.Logical, root.Root.SourcePath, mapped ?? string.Empty,
                mapping?.Origin ?? PathMappingOrigin.Default, status));
        }

        return mapper;
    }

    // --- Extraction of comparable files ------------------------------------------------------------------

    private async Task ExtractComparablesAsync(RestoreRequest request, List<ArtifactSummary> selected, PlanContext context, CancellationToken cancellationToken)
    {
        var wanted = new List<ArchiveEntry>();
        foreach (var summary in selected.Where(s => s.Record.Kind != CaptureKind.InventoryOnly && s.Record.Status != "Blocked"))
        {
            var entries = BackupReader.ReadEntries(request.Overview, summary.Record.Key, 0, int.MaxValue);
            context.Entries[summary.Record.Key] = entries;

            foreach (var entry in entries.Where(e => e.EntryType == ArchiveEntryType.File && e.Size <= MaxComparedBytes))
            {
                var name = Path.GetFileName(entry.ArchivePath);
                var isRepository = summary.Record.Artifact.Kind == ArtifactKind.Repository;
                var comparable = isRepository
                    ? IsGitPointer(entry.RelativePath)
                    : name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);
                if (comparable)
                {
                    wanted.Add(entry);
                }
            }
        }

        if (wanted.Count == 0)
        {
            return;
        }

        var folder = Path.Combine(request.WorkFolder, $"compare-{Guid.NewGuid():N}");
        CreatePrivateDirectory(folder);
        var result = await archive.ExtractSelectedAsync(new ArchiveExtractRequest(request.Overview.ArchivePath, request.Password, folder,
            [.. wanted.Select(e => e.ArchivePath)]), null, cancellationToken).ConfigureAwait(false);

        foreach (var entry in wanted)
        {
            var file = result.Files.FirstOrDefault(f => string.Equals(f.ArchivePath, ArchivePathValidator.Normalize(entry.ArchivePath), StringComparison.OrdinalIgnoreCase));
            if (file is null || !string.Equals(file.Sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                context.Finding(FindingSeverity.Blocking, [entry.ArtifactId], null, $"{entry.ArchivePath} in the backup does not match its recorded hash.",
                    "The backup may be damaged; restoring from it could write corrupted settings.", ["Copy the backup again from its source and open it again."], canRecheck: false);
                context.Blocked.Add(entry.ArtifactId);
                continue;
            }

            context.Contents[entry.ArchivePath] = File.ReadAllBytes(file.DestinationPath);
        }

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    internal static bool IsGitPointer(string relative)
        => relative.Equals(".git", StringComparison.OrdinalIgnoreCase)
           || (relative.StartsWith(@".git\worktrees\", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(@"\gitdir", StringComparison.OrdinalIgnoreCase))
           || (relative.StartsWith(@".git\modules\", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(@"\config", StringComparison.OrdinalIgnoreCase));

    // --- Host applications -----------------------------------------------------------------------------

    /// <returns>The id of the manual "install the host application" step the artifact depends on, if one is needed.</returns>
    private static string? HostPrerequisite(MigrationArtifact artifact, PlanContext context)
    {
        var toolId = artifact.OwnerToolId;
        if (toolId is "devbr" or "windows" or "custom" or "git" || artifact.Kind is ArtifactKind.Repository or ArtifactKind.Inventory or ArtifactKind.EnvironmentVariables or ArtifactKind.Credentials)
        {
            return null;
        }

        if (KnownTools.Get(toolId) is not { } tool)
        {
            return null;
        }

        var stepId = $"host:{toolId}";
        if (context.Installed.TryGetValue(toolId, out var installs))
        {
            CheckVersion(tool, installs, artifact, context);
            return null;
        }

        if (!context.HostSteps.Contains(stepId))
        {
            context.HostSteps.Add(stepId);
            var sourceVersion = context.SourceInventory.FirstOrDefault(i => i.ToolId == toolId)?.Version;
            context.Add(new PlannedOperation(
                new RestoreOperation(stepId, artifact.Id, [], tool.DisplayName, RestoreAction.ManualStep, ConflictDecision.NoConflict, PrivilegeRequirement.User, Reversibility.Irreversible, "installed"),
                $"Install {tool.DisplayName}", $"{(sourceVersion is null ? string.Empty : $"Version {sourceVersion} was used on the old computer. ")}To install: {PackageRecipes.HostHint(toolId)}.",
                null, null, 0, null, null, true, []));
        }

        context.Finding(FindingSeverity.Blocking, [artifact.Id], tool.DisplayName,
            $"{tool.DisplayName} is not installed on this computer.",
            $"Its settings would have nothing to apply to, and the application may overwrite them on first start.",
            [$"Install {tool.DisplayName} ({PackageRecipes.HostHint(toolId)}).", "Start it once, close it, then choose Recheck."],
            prerequisiteKey: stepId);
        context.Blocked.Add(artifact.Id);
        return stepId;
    }

    private static void CheckVersion(KnownTool tool, List<InventoryItem> installs, MigrationArtifact artifact, PlanContext context)
    {
        var sourceVersion = context.SourceInventory.FirstOrDefault(i => i.ToolId == tool.Id && i.Version is not null)?.Version;
        var targetVersion = installs.Select(i => i.Version).FirstOrDefault(v => v is not null);
        if (sourceVersion is null || targetVersion is null || !context.VersionWarned.Add(tool.Id))
        {
            return;
        }

        if (CompareVersions(targetVersion, sourceVersion) < 0)
        {
            context.Finding(FindingSeverity.Warning, [artifact.Id], tool.DisplayName,
                $"{tool.DisplayName} {targetVersion} on this computer is older than {sourceVersion} on the old computer.",
                "Settings written by a newer version can include options this version does not understand.",
                [$"Update {tool.DisplayName} to {sourceVersion} or later, then choose Recheck.", "Or continue; unknown settings are usually ignored."]);
        }
    }

    // --- Verified versions ------------------------------------------------------------------------------

    /// <summary>
    /// Whether the artifact's adapter may merge or rewrite it. That needs a verified host version: the one
    /// installed here (or, before it is installed, the one the backup came from); a version recorded on the
    /// old computer must be verified too. Otherwise the artifact falls back to whole-file handling (or
    /// inventory for the environment) and one finding per tool explains why.
    /// </summary>
    private static bool SemanticHandlingAllowed(MigrationArtifact artifact, PlanContext context)
    {
        if (DiscoveryEngine.DefaultAdapters.FirstOrDefault(a => a.Support.HostToolId == artifact.OwnerToolId) is not { Support.HasSemanticHandling: true } adapter)
        {
            return true;
        }

        var support = adapter.Support;
        if (!context.SemanticGates.TryGetValue(support.HostToolId, out var problem))
        {
            problem = context.SemanticGates[support.HostToolId] = UnverifiedVersionProblem(support, context);
        }

        if (problem is null)
        {
            return true;
        }

        var windows = support.HostToolId == "windows";
        context.Finding(FindingSeverity.Warning, [artifact.Id], support.HostName, problem,
            windows
                ? "DevBR changes environment variables and PATH only on Windows versions it has verified, so they are listed for reference instead."
                : $"DevBR merges settings and rewrites paths only for versions whose file formats it has verified ({support.VersionText}). These items are restored as whole files instead: this computer's files are kept unless you choose to replace them or restore the backup's copy alongside, and files are written exactly as captured.",
            windows
                ? ["Set the variables you need yourself after restoring."]
                : [$"Install a verified version of {support.HostName} ({support.VersionText}), then choose Recheck.", "Or continue, and choose Replace or Restore alongside for the files you want from the backup."],
            prerequisiteKey: $"unverified-version:{support.HostToolId}");
        return false;
    }

    private static string? UnverifiedVersionProblem(AdapterSupport support, PlanContext context)
    {
        var hostCategories = new[] { InventoryCategory.DeveloperTool, InventoryCategory.Runtime, InventoryCategory.PackageManager, InventoryCategory.Application };
        string? sourceVersion;
        string? targetVersion;
        bool installed;
        if (support.HostToolId == "windows")
        {
            sourceVersion = context.SourceInventory.FirstOrDefault(i => i.Category == InventoryCategory.SystemFact && i.Properties?.ContainsKey("architecture") == true)?.Version;
            targetVersion = context.Request.Target.Info.OsBuild;
            installed = true;
        }
        else
        {
            sourceVersion = context.SourceInventory.FirstOrDefault(i => i.ToolId == support.HostToolId && hostCategories.Contains(i.Category) && i.Version is not null)?.Version;
            installed = context.Installed.TryGetValue(support.HostToolId, out var installs);
            targetVersion = installs?.Select(i => i.Version).FirstOrDefault(v => v is not null);
        }

        var name = support.HostName;
        var outcome = support.HostToolId == "windows" ? "environment variables and PATH will be listed, not changed" : "its settings will be restored as whole files, not merged";
        if (installed && support.Evaluate(targetVersion) == VersionSupport.Unsupported)
        {
            return $"{name} {targetVersion} on this computer is not a version DevBR has verified ({support.VersionText}); {outcome}.";
        }

        if (sourceVersion is not null && support.Evaluate(sourceVersion) == VersionSupport.Unsupported)
        {
            return $"The backup's settings come from {name} {sourceVersion}, which is not a version DevBR has verified ({support.VersionText}); {outcome}.";
        }

        if (installed && support.Evaluate(targetVersion) == VersionSupport.Unknown)
        {
            return $"DevBR could not determine which version of {name} is on this computer; {outcome}.";
        }

        return !installed && support.Evaluate(sourceVersion) == VersionSupport.Unknown
            ? $"DevBR could not determine which version of {name} the backup's settings come from; {outcome}."
            : null;
    }

    public static int CompareVersions(string a, string b)
    {
        static long[] Parts(string v) => [.. DiscoveryEngine.NormalizeVersion(v).Split('.').Select(p => long.TryParse(new string([.. p.TakeWhile(char.IsDigit)]), out var n) ? n : 0)];
        var x = Parts(a);
        var y = Parts(b);
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    }

    // --- Inventory --------------------------------------------------------------------------------------

    private static void PlanInventory(ArtifactIndexRecord record, PlanContext context)
    {
        if (record.Artifact.Id != DiscoveryEngine.InventoryArtifactId)
        {
            context.Finding(FindingSeverity.Information, [record.Artifact.Id], null, $"{record.Artifact.DisplayName}: recorded for reference.",
                record.Artifact.Description ?? "Inventory only.", ["Reinstall these yourself if you need them."], canRecheck: false);
            return;
        }

        foreach (var item in context.SourceInventory.Where(i => i.Category is InventoryCategory.DeveloperTool or InventoryCategory.Runtime or InventoryCategory.PackageManager or InventoryCategory.Application))
        {
            var present = item.ToolId is not null
                ? context.Installed.ContainsKey(item.ToolId)
                : context.InstalledNames.Contains(item.Name);
            if (!present && context.ReinstallSeen.Add(item.ToolId ?? item.Name))
            {
                context.Reinstall.Add(new ReinstallGuidance(item.Name, item.Version, item.ToolId is null ? "Reinstall from its publisher if you still need it." : $"To install: {PackageRecipes.HostHint(item.ToolId)}."));
            }
        }
    }

    // --- Environment -------------------------------------------------------------------------------------

    private static void PlanEnvironment(ArtifactIndexRecord record, PlanContext context)
    {
        var entries = context.Entries.GetValueOrDefault(record.Key) ?? [];
        var entry = entries.FirstOrDefault(e => e.ArchivePath.EndsWith("/environment.json", StringComparison.Ordinal));
        if (entry is null || !context.Contents.TryGetValue(entry.ArchivePath, out var bytes))
        {
            return;
        }

        var variables = JsonSerializer.Deserialize<List<CapturedVariable>>(bytes, BackupRunner.IndexJson) ?? [];

        // Environment and PATH merges are semantic edits: on an unverified Windows build they become inventory only.
        if (!SemanticHandlingAllowed(record.Artifact, context))
        {
            var names = variables.Where(v => !SystemDefinedVariables.Contains(v.Name)).Select(v => $"{v.Name} ({v.Scope.ToLowerInvariant()})").ToList();
            if (names.Count > 0)
            {
                context.Finding(FindingSeverity.Information, [record.Artifact.Id], null,
                    $"{record.Artifact.DisplayName} are listed for reference only: {string.Join(", ", names.Take(6))}{(names.Count > 6 ? "…" : string.Empty)}.",
                    "No variables or PATH entries are changed on an unverified Windows version.",
                    ["Set the variables you need yourself, or restore onto a verified Windows version."], canRecheck: false);
            }

            return;
        }

        var target = context.Request.Target;
        var disabled = new List<string>();
        var unresolved = new List<string>();

        foreach (var variable in variables)
        {
            var hive = variable.Scope == "Machine" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            var privilege = hive == RegistryHive.LocalMachine ? PrivilegeRequirement.Elevated : PrivilegeRequirement.User;
            var existing = target.ReadEnvironment(hive);
            var kind = variable.Kind == RegistryValueKind.ExpandString ? "REG_EXPAND_SZ" : "REG_SZ";

            if (SystemDefinedVariables.Contains(variable.Name))
            {
                continue;
            }

            if (variable.Name.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                var current = (existing.GetValueOrDefault("Path")?.AsString() ?? string.Empty)
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(e => Paths.Normalize(target.Expand(e)).TrimEnd('\\'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var raw in variable.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var (mapped, outcome) = context.Rewriter.Rewrite(raw);
                    if (outcome == RewriteOutcome.Unresolved)
                    {
                        unresolved.Add(raw);
                        continue;
                    }

                    if (outcome == RewriteOutcome.Rewritten)
                    {
                        context.Rewrites.Add(new PathRewrite(record.Artifact.Id, $"Path ({variable.Scope})", raw, mapped, outcome));
                    }

                    var expanded = Paths.Normalize(target.Expand(mapped)).TrimEnd('\\');
                    if (current.Contains(expanded))
                    {
                        continue; // already on PATH: nothing to do, so a second restore adds nothing
                    }

                    var exists = target.FileSystem.DirectoryExists(expanded);
                    if (!exists)
                    {
                        disabled.Add(mapped);
                    }

                    context.Add(new PlannedOperation(
                        new RestoreOperation($"{record.Key}:path:{variable.Scope}:{mapped.ToUpperInvariant()}", record.Artifact.Id, [], $"PATH ({variable.Scope})", RestoreAction.AppendPathEntry,
                            ConflictDecision.NoConflict, privilege, Reversibility.RollbackCapable, mapped),
                        $"Add to {variable.Scope.ToLowerInvariant()} PATH: {mapped}", exists ? "Appended after the existing entries." : "Held back until this folder exists.",
                        entry.ArchivePath, null, 0, null, null, exists, [], mapped));
                    current.Add(expanded);
                }

                continue;
            }

            var (value, valueOutcome) = context.Rewriter.Rewrite(variable.Value);
            if (valueOutcome == RewriteOutcome.Rewritten)
            {
                context.Rewrites.Add(new PathRewrite(record.Artifact.Id, $"{variable.Name} ({variable.Scope})", variable.Value, value, valueOutcome));
            }
            else if (valueOutcome == RewriteOutcome.Unresolved)
            {
                unresolved.Add($"{variable.Name}={variable.Value}");
            }

            var opId = $"{record.Key}:env:{variable.Scope}:{variable.Name.ToUpperInvariant()}";
            var current2 = existing.GetValueOrDefault(variable.Name)?.AsString();
            var shown = record.Artifact.Sensitivity == Sensitivity.Credential || SecretDetector.IsSecret(variable.Name, value) ? "[hidden]" : value;

            // A value pointing at a location with no destination here is held back rather than set to a dead path.
            var resolved = valueOutcome != RewriteOutcome.Unresolved;
            if (current2 is null)
            {
                context.Add(new PlannedOperation(
                    new RestoreOperation(opId, record.Artifact.Id, [], $"ENV:{variable.Scope}:{variable.Name}", RestoreAction.SetEnvironmentVariable,
                        ConflictDecision.NoConflict, privilege, Reversibility.RollbackCapable, "absent"),
                    $"Set {variable.Name} ({variable.Scope.ToLowerInvariant()})",
                    resolved ? $"{shown} · {kind}" : $"{shown} · held back until its location is mapped to this computer",
                    entry.ArchivePath, Application.Machine.RestoreEffects.Sha256(value), 0, null, null, resolved, [], value, variable.Kind == RegistryValueKind.ExpandString));
            }
            else if (!string.Equals(current2, value, StringComparison.Ordinal))
            {
                var decision = context.Decision(opId, ConflictDecision.KeepExisting);
                var use = decision == ConflictDecision.UseBackup;
                context.Add(new PlannedOperation(
                    new RestoreOperation(opId, record.Artifact.Id, [], $"ENV:{variable.Scope}:{variable.Name}", use ? RestoreAction.SetEnvironmentVariable : RestoreAction.Skip,
                        decision, privilege, Reversibility.RollbackCapable, $"value:{Hash(current2)}"),
                    $"{variable.Name} ({variable.Scope.ToLowerInvariant()}) differs", use ? $"Will be set to {shown}" : "This computer's value is kept.",
                    entry.ArchivePath, use ? Application.Machine.RestoreEffects.Sha256(value) : null, 0, null, null, resolved, [ConflictDecision.KeepExisting, ConflictDecision.UseBackup], value, variable.Kind == RegistryValueKind.ExpandString));
            }
        }

        if (disabled.Count > 0)
        {
            context.Finding(FindingSeverity.Warning, [record.Artifact.Id], null,
                $"{disabled.Count} PATH {(disabled.Count == 1 ? "entry points" : "entries point")} to folders that do not exist here: {string.Join(", ", disabled.Take(4))}.",
                "Adding missing folders to PATH has no effect and can hide problems.",
                ["Install the tools that create these folders, then choose Recheck.", "Entries stay held back until their folders exist."]);
        }

        if (unresolved.Count > 0)
        {
            context.Finding(FindingSeverity.Warning, [record.Artifact.Id], null,
                $"{unresolved.Count} environment {(unresolved.Count == 1 ? "value refers" : "values refer")} to locations with no destination here: {string.Join(", ", unresolved.Take(3))}.",
                "DevBR does not guess where these paths should point on this computer.",
                ["Add a folder mapping for the drive or folder they use, then choose Recheck."]);
        }
    }

    // --- Repositories -------------------------------------------------------------------------------------

    private static void PlanRepository(ArtifactSummary summary, PlanContext context)
    {
        var record = summary.Record;
        var root = record.Roots.FirstOrDefault();
        if (root is null)
        {
            return;
        }

        var destination = context.Mapper.Map(root.SourcePath);
        if (destination is null)
        {
            context.Finding(FindingSeverity.Blocking, [record.Artifact.Id], null, $"{record.Artifact.DisplayName} has no destination on this computer ({root.SourcePath}).",
                "Its drive or folder does not exist here.", [$"Add a folder mapping for {root.SourcePath} (for example to a folder under your profile), then choose Recheck."]);
            context.Blocked.Add(record.Artifact.Id);
            return;
        }

        var fs = context.Request.Target.FileSystem;
        if (fs.DirectoryExists(destination) && SafeChildren(fs, destination).Any())
        {
            context.Finding(FindingSeverity.Blocking, [record.Artifact.Id], null, $"{destination} already exists and is not empty.",
                "Repositories are restored only into empty or new folders; two .git folders are never merged.",
                [$"Map {root.SourcePath} to a new folder, or move the existing folder away, then choose Recheck."]);
            context.Blocked.Add(record.Artifact.Id);
            return;
        }

        if (fs.FileExists(destination))
        {
            context.Finding(FindingSeverity.Blocking, [record.Artifact.Id], null, $"{destination} is a file.", "A repository needs a folder at that location.",
                [$"Map {root.SourcePath} somewhere else, then choose Recheck."]);
            context.Blocked.Add(record.Artifact.Id);
            return;
        }

        // Linked worktrees and submodules record absolute or relative git directories; only those references are rewritten.
        foreach (var entry in (context.Entries.GetValueOrDefault(record.Key) ?? []).Where(e => IsGitPointer(e.RelativePath)))
        {
            if (!context.Contents.TryGetValue(entry.ArchivePath, out var bytes))
            {
                continue;
            }

            var text = System.Text.Encoding.UTF8.GetString(bytes).Trim();
            var pointer = text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) ? text["gitdir:".Length..].Trim()
                : entry.RelativePath.EndsWith(@"\gitdir", StringComparison.OrdinalIgnoreCase) ? text
                : null;
            if (pointer is null || !(pointer.Length > 2 && pointer[1] == ':'))
            {
                continue; // relative references move with the repository unchanged
            }

            var (after, outcome) = context.Rewriter.Rewrite(pointer);
            context.Rewrites.Add(new PathRewrite(record.Artifact.Id, $"{record.Artifact.DisplayName}: {entry.RelativePath}", pointer, after, outcome == RewriteOutcome.Unchanged ? RewriteOutcome.Unchanged : outcome));
            if (outcome == RewriteOutcome.Unresolved)
            {
                context.Finding(FindingSeverity.Warning, [record.Artifact.Id], null,
                    $"{record.Artifact.DisplayName} points to Git data at {pointer}, which has no destination here.",
                    "The linked worktree or submodule will not work until its main repository is restored where it expects.",
                    ["Select and map the repository that owns this Git data, then choose Recheck."]);
            }
        }

        foreach (var dependency in record.Artifact.Dependencies.Where(d => !context.SelectedIds.Contains(d)))
        {
            context.Finding(FindingSeverity.Warning, [record.Artifact.Id], null,
                $"{record.Artifact.DisplayName} depends on a repository that is not selected for restore.",
                "Its Git data lives in that other repository.", ["Select the repository it depends on as well."]);
        }

        if (!context.Installed.ContainsKey("git") && context.GitWarned is false)
        {
            context.GitWarned = true;
            context.Finding(FindingSeverity.Warning, [record.Artifact.Id], "Git", "Git is not installed on this computer.",
                "Restored repositories are complete, but you need Git to work with them.", [$"Install Git ({PackageRecipes.HostHint("git")}), then choose Recheck."]);
        }

        context.Add(new PlannedOperation(
            new RestoreOperation($"{record.Key}:repository", record.Artifact.Id, [], destination, RestoreAction.RestoreRepository, ConflictDecision.NoConflict,
                PrivilegeRequirement.User, Reversibility.RollbackCapable, "absent-or-empty"),
            $"Restore {record.Artifact.DisplayName}", $"To {destination} · {Count(summary.EntryCount, "file")} incl. history, branches, stashes and untracked files",
            null, null, summary.Bytes, null, null, true, []));
    }

    // --- Extensions ----------------------------------------------------------------------------------------

    private static void PlanExtensions(ArtifactIndexRecord record, string? hostStep, PlanContext context)
    {
        var entry = (context.Entries.GetValueOrDefault(record.Key) ?? []).FirstOrDefault(e => e.ArchivePath.EndsWith("extensions.json", StringComparison.OrdinalIgnoreCase));
        if (entry is null || !context.Contents.TryGetValue(entry.ArchivePath, out var bytes) || JsonMerger.Parse(bytes) is not JsonArray list)
        {
            return;
        }

        var editor = record.Artifact.OwnerToolId;
        foreach (var extension in list.OfType<JsonObject>())
        {
            var id = (string?)extension["identifier"]?["id"];
            var version = (string?)extension["version"];
            if (id is null || context.TargetExtensions.Contains($"{editor}:{id}".ToLowerInvariant()))
            {
                continue;
            }

            var recipe = PackageRecipes.Extension(editor, id, version);
            if (recipe is null)
            {
                context.Finding(FindingSeverity.Information, [record.Artifact.Id], null, $"Extension '{id}' has an unrecognized identifier and is not installed automatically.",
                    "DevBR only installs extensions with well-formed marketplace identifiers.", [$"Install it yourself from the marketplace if you need it."], canRecheck: false);
                continue;
            }

            context.Add(new PlannedOperation(
                new RestoreOperation($"{record.Key}:ext:{id.ToLowerInvariant()}", record.Artifact.Id, hostStep is null ? [] : [hostStep], id, RestoreAction.InstallDependency,
                    ConflictDecision.NoConflict, PrivilegeRequirement.User, Reversibility.Irreversible, "not-installed"),
                $"Install extension {id}", $"{recipe.Preview} · downloads from the {DiscoveryText(editor)} marketplace", null, null, 0, recipe, null,
                !context.Blocked.Contains(record.Artifact.Id), []));
        }
    }

    // --- Configuration files ---------------------------------------------------------------------------------

    private static void PlanFiles(ArtifactSummary summary, string? hostStep, PlanContext context)
    {
        var record = summary.Record;
        var target = context.Request.Target;
        var blockedArtifact = context.Blocked.Contains(record.Artifact.Id);
        var unresolvedRoots = new HashSet<int>();
        var undeclared = new List<string>();

        foreach (var entry in context.Entries.GetValueOrDefault(record.Key) ?? [])
        {
            var rootIndex = RootIndex(entry.ArchivePath);
            var root = record.Roots.FirstOrDefault(r => r.Index == rootIndex);
            if (root is null)
            {
                continue;
            }

            var sourcePath = entry.RelativePath.Length == 0 ? root.SourcePath : Paths.Combine(root.SourcePath, entry.RelativePath);
            var destination = context.Mapper.Map(sourcePath);
            if (destination is null)
            {
                if (unresolvedRoots.Add(rootIndex))
                {
                    context.Finding(FindingSeverity.Blocking, [record.Artifact.Id], null, $"{record.Artifact.DisplayName}: {root.SourcePath} has no destination on this computer.",
                        "Its drive or folder does not exist here, and DevBR does not guess a new location.",
                        [$"Add a folder mapping for {root.SourcePath}, then choose Recheck."]);
                    context.Blocked.Add(record.Artifact.Id);
                }

                continue;
            }

            var opId = $"{record.Key}:file:{entry.ArchivePath[(entry.ArchivePath.IndexOf('/', 8) + 1)..]}";
            var dependsOn = hostStep is null ? Array.Empty<string>() : [hostStep];

            if (entry.EntryType == ArchiveEntryType.Directory)
            {
                if (!target.FileSystem.DirectoryExists(destination))
                {
                    context.Add(new PlannedOperation(new RestoreOperation(opId, record.Artifact.Id, dependsOn, destination, RestoreAction.CreateFile, ConflictDecision.NoConflict,
                        PrivilegeRequirement.User, Reversibility.RollbackCapable, "directory"), $"Create folder {destination}", null, entry.ArchivePath, null, 0, null, null, !blockedArtifact, []));
                }

                continue;
            }

            var fileName = Path.GetFileName(destination);
            var profile = MergeProfile.For(record.Artifact.Id, fileName);

            // Merges and path rewrites only for verified host versions; otherwise the file is handled whole and written as captured.
            var verbatim = profile is not null && !SemanticHandlingAllowed(record.Artifact, context);
            if (verbatim)
            {
                profile = null;
            }

            context.Contents.TryGetValue(entry.ArchivePath, out var content);
            var backupNode = profile is not null && content is not null ? JsonMerger.Parse(content) : null;

            if (backupNode is not null)
            {
                foreach (var (field, before, after, outcome) in JsonMerger.RewritePaths(backupNode, profile!, context.Rewriter))
                {
                    context.Rewrites.Add(new PathRewrite(record.Artifact.Id, $"{fileName}: {field}", before, after, outcome));
                }

                foreach (var prefix in context.SourcePrefixes)
                {
                    undeclared.AddRange(JsonMerger.UndeclaredPaths(backupNode, prefix).Select(p => $"{fileName}: {p.Path}"));
                }
            }

            if (!target.FileSystem.FileExists(destination))
            {
                context.Add(new PlannedOperation(
                    new RestoreOperation(opId, record.Artifact.Id, dependsOn, destination, RestoreAction.CreateFile, ConflictDecision.NoConflict, PrivilegeRequirement.User,
                        Reversibility.RollbackCapable, "absent"),
                    $"Create {destination}", verbatim ? $"{Size(entry.Size)} · copied as captured (no merge or path rewrite)" : Size(entry.Size), entry.ArchivePath, entry.Sha256, entry.Size, null, null, !blockedArtifact, [],
                    Verbatim: verbatim));
                continue;
            }

            var targetHash = HashFile(target, destination);
            if (string.Equals(targetHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                context.Add(new PlannedOperation(
                    new RestoreOperation(opId, record.Artifact.Id, dependsOn, destination, RestoreAction.Skip, ConflictDecision.NoConflict, PrivilegeRequirement.User,
                        Reversibility.RollbackCapable, $"sha256:{targetHash}"),
                    $"{destination} is already identical", null, entry.ArchivePath, entry.Sha256, entry.Size, null, null, true, []));
                continue;
            }

            var targetBytes = ReadBounded(target, destination);
            var targetNode = backupNode is not null && targetBytes is not null ? JsonMerger.Parse(targetBytes) : null;
            var expected = $"sha256:{targetHash}";

            if (backupNode is not null && targetNode is not null)
            {
                // Structured merge: add what is missing, keep this computer's values unless told otherwise.
                var decision = context.Decision(opId, ConflictDecision.Merge);
                if (decision is ConflictDecision.Merge or ConflictDecision.UseBackup)
                {
                    var (_, changes) = JsonMerger.Merge(targetNode, backupNode, profile!, preferBackup: decision == ConflictDecision.UseBackup);
                    var preview = new MergePreview(destination, changes);
                    var nothingToDo = changes.All(c => c.Kind is MergeChangeKind.Unchanged or MergeChangeKind.ConflictKeptTarget);
                    context.Add(new PlannedOperation(
                        new RestoreOperation(opId, record.Artifact.Id, dependsOn, destination, nothingToDo ? RestoreAction.Skip : RestoreAction.MergeStructuredSettings, decision,
                            PrivilegeRequirement.User, Reversibility.RollbackCapable, expected),
                        nothingToDo ? $"{destination}: nothing new to add" : $"Merge into {destination}",
                        $"{preview.Added} added · {preview.Conflicts} different (this computer's values {(decision == ConflictDecision.UseBackup ? "replaced" : "kept")})",
                        entry.ArchivePath, entry.Sha256, entry.Size, null, preview, !blockedArtifact,
                        [ConflictDecision.Merge, ConflictDecision.UseBackup, ConflictDecision.KeepExisting, ConflictDecision.RestoreAlongside]));
                    continue;
                }

                AddWholeFileDecision(decision);
                continue;
            }

            AddWholeFileDecision(context.Decision(opId, ConflictDecision.KeepExisting));

            void AddWholeFileDecision(ConflictDecision decision)
            {
                var (action, where, title) = decision switch
                {
                    ConflictDecision.UseBackup => (RestoreAction.ReplaceFile, destination, $"Replace {destination}"),
                    ConflictDecision.RestoreAlongside => (RestoreAction.RestoreAlongside, AlongsidePath(destination), $"Restore next to {destination}"),
                    _ => (RestoreAction.Skip, destination, $"Keep {destination}"),
                };
                var allowed = backupNode is not null
                    ? new[] { ConflictDecision.Merge, ConflictDecision.UseBackup, ConflictDecision.KeepExisting, ConflictDecision.RestoreAlongside }
                    : [ConflictDecision.KeepExisting, ConflictDecision.UseBackup, ConflictDecision.RestoreAlongside];
                context.Add(new PlannedOperation(
                    new RestoreOperation(opId, record.Artifact.Id, dependsOn, where, action, decision == ConflictDecision.Merge ? ConflictDecision.KeepExisting : decision,
                        PrivilegeRequirement.User, Reversibility.RollbackCapable, expected),
                    title, action == RestoreAction.Skip ? "This computer's file differs and is kept." : action == RestoreAction.RestoreAlongside ? $"Saved as {Path.GetFileName(where)}" : "The current file is kept for rollback.",
                    entry.ArchivePath, entry.Sha256, entry.Size, null, null, !blockedArtifact, allowed, Verbatim: verbatim));
            }
        }

        foreach (var (artifactId, file) in context.McpCandidates(record))
        {
            context.Mcp.AddRange(McpAnalyzer.Analyze(artifactId, file.Name, file.Node));
        }

        if (undeclared.Count > 0)
        {
            context.Finding(FindingSeverity.Information, [record.Artifact.Id], null,
                $"{record.Artifact.DisplayName} mentions paths from the old computer that are not rewritten: {string.Join("; ", undeclared.Take(3))}{(undeclared.Count > 3 ? "…" : string.Empty)}.",
                "Only fields known to hold paths are rewritten; arguments and commands are restored as written.",
                ["Review these values after restoring if the folders moved."], canRecheck: false);
        }
    }

    // --- MCP runtimes ----------------------------------------------------------------------------------------

    private static void PlanMcpRuntimes(PlanContext context)
    {
        foreach (var group in context.Mcp.Where(m => m.RequiredToolId is not null).GroupBy(m => m.RequiredToolId!))
        {
            if (context.Installed.ContainsKey(group.Key))
            {
                continue;
            }

            var tool = KnownTools.Get(group.Key);
            var servers = string.Join(", ", group.Select(s => s.Name).Distinct());
            var recipe = context.WinGetAllowed ? PackageRecipes.Runtime(group.Key) : null;

            // Usable items first: the install is attributed to an item that can actually be restored.
            var artifacts = group.Select(s => s.ArtifactId).Distinct().OrderBy(a => context.Blocked.Contains(a)).ToList();

            // Held back while every item that needs it is blocked: installing it would not help yet.
            var usable = !context.Blocked.Contains(artifacts[0]);

            if (recipe is not null)
            {
                context.Add(new PlannedOperation(
                    new RestoreOperation($"runtime:{group.Key}", artifacts[0], [], recipe.PackageId, RestoreAction.InstallDependency, ConflictDecision.NoConflict,
                        recipe.RequiresElevation ? PrivilegeRequirement.Elevated : PrivilegeRequirement.User, Reversibility.Irreversible, "not-installed"),
                    $"Install {recipe.DisplayName}", $"{recipe.Preview} · from {recipe.Source}{(recipe.RequiresElevation ? " · needs administrator approval" : string.Empty)}. Installers cannot be rolled back.",
                    null, null, 0, recipe, null, usable, []));
            }

            context.Finding(FindingSeverity.Warning, artifacts, tool?.DisplayName ?? group.Key,
                $"MCP servers {servers} need {tool?.DisplayName ?? group.Key}, which is not installed.",
                "Their configuration is restored, but they will not start until it is installed.",
                recipe is not null
                    ? [$"Approve the planned installation of {recipe.DisplayName}, or install it yourself and choose Recheck."]
                    : [$"Install {tool?.DisplayName ?? group.Key} ({PackageRecipes.HostHint(group.Key)}), then choose Recheck."]);
        }

        foreach (var server in context.Mcp.Where(m => m.Kind is McpServerKind.Unsupported))
        {
            context.Finding(FindingSeverity.Information, [server.ArtifactId], null, $"MCP server '{server.Name}' ({server.File}) needs manual attention.",
                server.Detail, ["After restoring, confirm the command it uses exists on this computer."], canRecheck: false);
        }

        foreach (var server in context.Mcp.Where(m => m.Kind is McpServerKind.LocalExecutable))
        {
            context.Finding(FindingSeverity.Information, [server.ArtifactId], null, $"MCP server '{server.Name}' runs a local program.",
                server.Detail, ["Restore or reinstall that program at the same path, or edit the server definition after restoring."], canRecheck: false);
        }
    }

    // --- Cross-cutting checks -----------------------------------------------------------------------------

    private static void CheckRunningApplications(PlanContext context)
    {
        var running = context.Request.Target.GetRunningProcessNames();
        var touched = context.Operations.Where(o => o.Enabled && o.Operation.Action is RestoreAction.CreateFile or RestoreAction.ReplaceFile or RestoreAction.MergeStructuredSettings)
            .Select(o => context.ArtifactOwner(o.Operation.ArtifactId)).Distinct();

        foreach (var toolId in touched.OfType<string>())
        {
            if (KnownTools.Get(toolId) is { } tool && tool.Executables.FirstOrDefault(running.Contains) is { } exe)
            {
                context.Finding(FindingSeverity.Warning, [.. context.ArtifactsOf(toolId)], tool.DisplayName, $"{tool.DisplayName} is running ({exe}).",
                    "It can overwrite restored settings when it closes, or hold files open.", [$"Close {tool.DisplayName}, then choose Recheck."]);
            }
        }
    }

    private static void CheckPrivilege(PlanContext context)
    {
        var elevated = context.Operations.Where(o => o.Enabled && o.Operation.Privilege == PrivilegeRequirement.Elevated && o.Operation.Action != RestoreAction.Skip).ToList();
        if (elevated.Count > 0 && !context.Request.Target.Info.IsElevated)
        {
            context.Finding(FindingSeverity.Information, [.. elevated.Select(o => o.Operation.ArtifactId).Distinct()], "Administrator approval",
                $"{Count(elevated.Count, "change")} need administrator approval (machine environment or installers).",
                "Windows will ask once when those steps run. Declining skips only them; everything else is still restored.",
                ["Approve the Windows prompt during restore, or decline to restore user-level items only."], canRecheck: false);
        }
    }

    private static void CheckCredentials(List<ArtifactSummary> selected, PlanContext context)
    {
        var steps = selected.Select(s => s.Record.Artifact.OwnerToolId).Distinct().Select(t => Reauthentication.GetValueOrDefault(t)).OfType<string>().ToList();
        if (steps.Count > 0)
        {
            context.Finding(FindingSeverity.Information, [], "Sign-in",
                "Sign-ins are not transferred, so some tools will ask you to sign in again.",
                "Credentials are tied to the old computer or deliberately left out of backups.", steps, canRecheck: false);
        }
    }

    private static void CheckPolicies(PlanContext context)
    {
        var target = context.Request.Target;
        if (target.FileSystem.FileExists(Paths.Combine(target.Folders.ProgramData, "ClaudeCode", "managed-settings.json")))
        {
            context.Finding(FindingSeverity.Information, context.ArtifactsOf("claude-code").ToList(), "Organization policy",
                "Claude Code on this computer has organization-managed settings.", "Managed settings take precedence; DevBR never overrides them.",
                ["Restored personal settings apply only where your organization allows them."], canRecheck: false);
        }

        try
        {
            using var policy = target.Registry.OpenKey(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\VSCode");
            if (policy is not null)
            {
                context.Finding(FindingSeverity.Information, context.ArtifactsOf("vscode").ToList(), "Organization policy",
                    "VS Code on this computer is managed by policy.", "Policy-controlled settings win over restored ones.", ["No action needed."], canRecheck: false);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        if (!context.WinGetAllowed && context.Operations.Any(o => o.Recipe?.Kind == RecipeKind.WinGet))
        {
            context.Finding(FindingSeverity.Warning, [], "Package installation",
                "WinGet is unavailable or disabled by policy on this computer.", "DevBR only installs through package managers that are present and permitted.",
                ["Install the listed runtimes yourself, then choose Recheck."]);
        }
    }

    private static void CheckCapacityAndFilesystem(PlanContext context)
    {
        var target = context.Request.Target;
        var drives = target.FileSystem.GetFixedDrives().ToDictionary(d => Paths.Normalize(d.Root), StringComparer.OrdinalIgnoreCase);
        var writes = context.Operations.Where(o => o.Enabled && o.Operation.Action is RestoreAction.CreateFile or RestoreAction.ReplaceFile or RestoreAction.MergeStructuredSettings
            or RestoreAction.RestoreAlongside or RestoreAction.RestoreRepository).ToList();

        // Space for extraction, the new content, and rollback copies of anything replaced.
        foreach (var group in writes.GroupBy(o => Paths.Root(o.Operation.Target) ?? "?", StringComparer.OrdinalIgnoreCase))
        {
            var need = group.Sum(o => o.Size * (o.Operation.Action is RestoreAction.ReplaceFile or RestoreAction.MergeStructuredSettings ? 2 : 1)) + (64L * 1024 * 1024);
            if (!drives.TryGetValue(group.Key, out var drive))
            {
                context.Finding(FindingSeverity.Blocking, [.. group.Select(o => o.Operation.ArtifactId).Distinct()], null, $"Drive {group.Key} does not exist on this computer.",
                    "Restored files need a destination that exists.", ["Change the folder mapping to an existing drive, then choose Recheck."]);
                foreach (var artifactId in group.Select(o => o.Operation.ArtifactId))
                {
                    context.Blocked.Add(artifactId);
                }

                continue;
            }

            if (drive.FreeBytes < need)
            {
                context.Finding(FindingSeverity.Blocking, [.. group.Select(o => o.Operation.ArtifactId).Distinct()], null,
                    $"Drive {group.Key} has {Size(drive.FreeBytes)} free; the restore needs about {Size(need)}.",
                    "There must be room for the restored files and for rollback copies of anything replaced.",
                    ["Free up space or map large items to another drive, then choose Recheck."]);
            }

            if (string.Equals(drive.Format, "FAT32", StringComparison.OrdinalIgnoreCase) && group.Any(o => o.Size > Fat32MaxFile))
            {
                context.Finding(FindingSeverity.Blocking, [.. group.Select(o => o.Operation.ArtifactId).Distinct()], null, $"Drive {group.Key} uses FAT32 and cannot hold files over 4 GB.",
                    "Some restored files are larger than that.", ["Map those items to an NTFS drive, then choose Recheck."]);
            }
        }

        foreach (var collision in writes.GroupBy(o => o.Operation.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            context.Finding(FindingSeverity.Blocking, [.. collision.Select(o => o.Operation.ArtifactId).Distinct()], null,
                $"{Count(collision.Count(), "item")} would be written to the same place: {collision.Key}.",
                "Windows file names are not case-sensitive, so one would overwrite the other.", ["Deselect one of them or map it elsewhere, then choose Recheck."]);
            foreach (var op in collision)
            {
                context.Blocked.Add(op.Operation.ArtifactId);
            }
        }

        if (writes.Any(o => o.Operation.Target.Length >= 260) && !LongPathsEnabled(target))
        {
            context.Finding(FindingSeverity.Warning, [.. writes.Where(o => o.Operation.Target.Length >= 260).Select(o => o.Operation.ArtifactId).Distinct()], null,
                "Some destinations are longer than 260 characters and long paths are not enabled on this computer.",
                "Older tools may fail to open those files.", ["Enable long paths (Group Policy: Enable Win32 long paths), or map those items to a shorter folder, then choose Recheck."]);
        }
    }

    private static bool LongPathsEnabled(IMachine target)
    {
        try
        {
            using var key = target.Registry.OpenKey(RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue("LongPathsEnabled")?.AsInt64() == 1;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // --- Helpers ---------------------------------------------------------------------------------------------

    private static List<InventoryItem> ReadInventory(BackupOverview overview)
    {
        var path = Path.Combine(overview.CatalogFolder, "inventory.ndjson");
        if (!File.Exists(path))
        {
            return [];
        }

        return [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<InventoryItem>(l, BackupRunner.IndexJson)).OfType<InventoryItem>()];
    }

    private static int RootIndex(string archivePath)
    {
        var parts = archivePath.Split('/');
        return parts.Length > 2 && parts[2].StartsWith('r') && int.TryParse(parts[2][1..], out var index) ? index : -1;
    }

    private static IEnumerable<FileSystemEntry> SafeChildren(IMachineFileSystem fs, string directory)
    {
        try
        {
            return fs.EnumerateEntries(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static string? HashFile(IMachine machine, string path)
    {
        try
        {
            using var stream = machine.FileSystem.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[]? ReadBounded(IMachine machine, string path)
    {
        try
        {
            using var stream = machine.FileSystem.OpenRead(path);
            if (stream.Length > MaxComparedBytes)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string AlongsidePath(string destination)
    {
        var directory = Path.GetDirectoryName(destination) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(destination);

        // Dotfiles (.gitconfig) have no stem; "settings.json" becomes "settings.restored.json".
        return stem.Length == 0
            ? Path.Combine(directory, Path.GetFileName(destination) + ".restored")
            : Path.Combine(directory, $"{stem}.restored{Path.GetExtension(destination)}");
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];

    private static string Count(long count, string noun) => $"{count:N0} {noun}{(count == 1 ? string.Empty : "s")}";

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        1 => "1 byte",
        _ => $"{bytes} bytes",
    };

    private static string DiscoveryText(string toolId) => toolId switch { "cursor" => "Open VSX", _ => "Visual Studio" };

    private static void CreatePrivateDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.CreateDirectory(path);
    }

    /// <summary>Mutable state for one preflight run.</summary>
    private sealed class PlanContext(RestoreRequest request)
    {
        private readonly Dictionary<string, PreflightFinding> _findings = new(StringComparer.Ordinal);

        public RestoreRequest Request { get; } = request;

        public Dictionary<string, List<InventoryItem>> Installed { get; set; } = [];

        public HashSet<string> InstalledNames => [.. Installed.Values.SelectMany(v => v).Select(i => i.Name)];

        public HashSet<string> TargetExtensions { get; set; } = [];

        public List<InventoryItem> SourceInventory { get; set; } = [];

        public PathMapper Mapper { get; set; } = new([]);

        public PathRewriter Rewriter { get; set; } = new(new PathMapper([]));

        public Dictionary<string, IReadOnlyList<ArchiveEntry>> Entries { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, byte[]> Contents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<PlannedOperation> Operations { get; } = [];

        public List<PathMapping> MappingRows { get; } = [];

        public List<PathRewrite> Rewrites { get; } = [];

        public List<McpServerInfo> Mcp { get; } = [];

        public List<ReinstallGuidance> Reinstall { get; } = [];

        public HashSet<string> ReinstallSeen { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Blocked { get; } = new(StringComparer.Ordinal);

        public HashSet<string> HostSteps { get; } = new(StringComparer.Ordinal);

        public HashSet<string> VersionWarned { get; } = new(StringComparer.Ordinal);

        /// <summary>Per host tool: why semantic handling is off (null when its version is verified).</summary>
        public Dictionary<string, string?> SemanticGates { get; } = new(StringComparer.Ordinal);

        public bool GitWarned { get; set; }

        public IEnumerable<PreflightFinding> Findings => _findings.Values;

        public HashSet<string> SelectedIds => [.. Request.Overview.Artifacts.Where(a => Request.SelectedKeys.Contains(a.Record.Key)).Select(a => a.Record.Artifact.Id)];

        /// <summary>Source path prefixes that indicate a value still points at the old computer.</summary>
        public IEnumerable<string> SourcePrefixes => Mapper.Mappings.Where(m => !string.Equals(m.SourcePrefix, m.TargetPrefix, StringComparison.OrdinalIgnoreCase)).Select(m => m.SourcePrefix);

        public bool WinGetAllowed
        {
            get
            {
                if (!Installed.ContainsKey("winget"))
                {
                    return false;
                }

                try
                {
                    using var policy = Request.Target.Registry.OpenKey(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\AppInstaller");
                    return policy?.GetValue("EnableAppInstaller")?.AsInt64() != 0;
                }
                catch (UnauthorizedAccessException)
                {
                    return true;
                }
            }
        }

        public ConflictDecision Decision(string operationId, ConflictDecision fallback)
            => Request.Decisions.TryGetValue(operationId, out var decision) ? decision : fallback;

        public void Add(PlannedOperation operation) => Operations.Add(operation);

        public string? ArtifactOwner(string artifactId)
            => Request.Overview.Artifacts.FirstOrDefault(a => a.Record.Artifact.Id == artifactId)?.Record.Artifact.OwnerToolId;

        public IEnumerable<string> ArtifactsOf(string toolId)
            => Request.Overview.Artifacts.Where(a => Request.SelectedKeys.Contains(a.Record.Key) && a.Record.Artifact.OwnerToolId == toolId).Select(a => a.Record.Artifact.Id);

        /// <summary>Parsed JSON configuration files of an artifact that may declare MCP servers.</summary>
        public IEnumerable<(string ArtifactId, (string Name, JsonNode? Node) File)> McpCandidates(ArtifactIndexRecord record)
        {
            if (record.Artifact.Kind is not (ArtifactKind.McpConfiguration or ArtifactKind.Settings))
            {
                yield break;
            }

            foreach (var entry in Entries.GetValueOrDefault(record.Key) ?? [])
            {
                if (Contents.TryGetValue(entry.ArchivePath, out var bytes) && entry.ArchivePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    yield return (record.Artifact.Id, (Path.GetFileName(entry.ArchivePath), JsonMerger.Parse(bytes)));
                }
            }
        }

        public void Finding(FindingSeverity severity, IReadOnlyList<string> affected, string? prerequisite, string problem, string why, IReadOnlyList<string> nextSteps,
            bool canRecheck = true, string? prerequisiteKey = null)
        {
            // Findings about the same prerequisite are combined, listing every affected item once.
            var id = prerequisiteKey ?? $"{severity}:{problem}";
            if (_findings.TryGetValue(id, out var existing))
            {
                _findings[id] = existing with { AffectedArtifactIds = [.. existing.AffectedArtifactIds.Union(affected)] };
                return;
            }

            _findings[id] = new PreflightFinding(id, severity, affected, prerequisite, problem, why, nextSteps, canRecheck);
        }
    }
}
