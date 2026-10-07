using System.Globalization;
using DevBR.Application.Machine;
using DevBR.Backup.Capture;
using DevBR.Discovery.Support;
using DevBR.Domain;

namespace DevBR.Backup;

/// <summary>
/// Turns the user's selection into the exact capture plan shown before anything is copied: every file,
/// every exclusion and every finding. Planning reads metadata only (plus Git indexes); it writes nothing.
/// </summary>
public sealed class BackupPlanner
{
    public const string CustomOwner = "custom";
    private const int MaxDepth = 128;

    public BackupPlan Plan(BackupPlanRequest request, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var machine = request.Machine;
        var findings = new List<PlanFinding>();
        var encryption = new List<string>();
        var planned = new List<PlannedArtifact>();
        var capturedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedIds = request.Selected.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var items = request.Snapshot.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);

        var artifacts = request.Selected.Concat(request.CustomPaths.Select(p => CustomArtifact(machine, p))).ToList();

        // Repositories and custom folders last, so tool configuration inside them is attributed to its tool.
        var ordered = artifacts.OrderBy(a => a.Kind is ArtifactKind.Repository or ArtifactKind.CustomFiles ? 1 : 0).ToList();
        var index = 0;

        foreach (var artifact in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = $"a{++index:D4}";
            progress?.Report($"Planning {artifact.DisplayName}");

            if (artifact.Kind == ArtifactKind.Inventory)
            {
                planned.Add(new PlannedArtifact(artifact, key, CaptureKind.InventoryOnly, [], [], [], false));
                continue;
            }

            if (artifact.Kind == ArtifactKind.EnvironmentVariables || (artifact.Kind == ArtifactKind.Credentials && artifact.Id.StartsWith("environment:secret:", StringComparison.Ordinal)))
            {
                planned.Add(new PlannedArtifact(artifact, key, CaptureKind.Environment, [], [], [], false));
                if (artifact.Sensitivity == Sensitivity.Credential)
                {
                    encryption.Add($"{artifact.DisplayName} is a credential.");
                }

                continue;
            }

            if (artifact.Eligibility is BackupEligibility.Blocked or BackupEligibility.InventoryOnly)
            {
                findings.Add(new PlanFinding(FindingLevel.Information, artifact.Id, $"{artifact.DisplayName} is recorded in the inventory only; its files are not captured."));
                planned.Add(new PlannedArtifact(artifact, key, CaptureKind.InventoryOnly, [], [], [], false));
                continue;
            }

            var item = artifact.InventoryItemId is not null ? items.GetValueOrDefault(artifact.InventoryItemId) : null;
            var blocked = artifact.Kind == ArtifactKind.Repository && CheckRepository(artifact, item, selectedIds, request.Snapshot, findings);

            var roots = new List<PlannedRoot>();
            var files = new List<PlannedFile>();
            var exclusions = new List<AppliedExclusion>();
            var applyRules = artifact.Kind is ArtifactKind.Repository or ArtifactKind.CustomFiles;

            for (var r = 0; r < artifact.Roots.Count; r++)
            {
                var source = Resolve(machine.Folders, artifact.Roots[r]);
                var entry = machine.FileSystem.GetEntry(source);
                if (entry is null)
                {
                    findings.Add(new PlanFinding(FindingLevel.Warning, artifact.Id, $"{source} no longer exists and will be skipped.", "Run discovery again if this is unexpected."));
                    continue;
                }

                var root = new PlannedRoot(r, artifact.Roots[r], source, entry.IsDirectory);
                roots.Add(root);
                if (blocked)
                {
                    continue;
                }

                if (!entry.IsDirectory)
                {
                    if (request.ProtectedPaths.Any(p => Paths.IsUnder(source, p)))
                    {
                        continue;
                    }

                    if (entry.IsCloudPlaceholder)
                    {
                        findings.Add(new PlanFinding(FindingLevel.Warning, artifact.Id, $"{source} is a cloud-only file and was not downloaded.", "Make it available offline, then plan again."));
                    }
                    else if (capturedPaths.Add(source))
                    {
                        files.Add(new PlannedFile(r, source, string.Empty, entry.Length, entry.LastWriteUtc, false));
                    }

                    continue;
                }

                var tracked = applyRules && artifact.Kind == ArtifactKind.Repository ? ReadTracked(machine, source, item) : null;
                if (applyRules && artifact.Kind == ArtifactKind.Repository && tracked is null)
                {
                    findings.Add(new PlanFinding(FindingLevel.Information, artifact.Id,
                        $"The Git index of {source} could not be read, so no folders are excluded (tracked content is never dropped)."));
                }

                Walk(machine, artifact, root, source, string.Empty, 0, applyRules ? request.Exclusions : [], tracked,
                    artifact.Kind == ArtifactKind.Repository && tracked is null, request.ProtectedPaths, capturedPaths, files, exclusions, findings, cancellationToken);
            }

            if (!blocked)
            {
                Classify(artifact, encryption);
            }

            planned.Add(new PlannedArtifact(artifact, key, CaptureKind.Files, roots, files, exclusions, blocked));
        }

        if (planned.Any(p => p.Artifact.Kind is ArtifactKind.Repository or ArtifactKind.CustomFiles && !p.Blocked))
        {
            findings.Add(new PlanFinding(FindingLevel.Information, null,
                "Git history and personal files can contain secrets that automatic detection misses. Encrypt the backup if in doubt."));
        }

        return new BackupPlan(Guid.NewGuid(), machine, request.Snapshot, planned, findings, encryption, request.Exclusions);
    }

    /// <summary>Resolves a logical path on the source machine.</summary>
    public static string Resolve(MachineFolders folders, LogicalPath path)
    {
        var root = path.Root switch
        {
            LogicalRootKind.RoamingAppData => folders.RoamingAppData,
            LogicalRootKind.LocalAppData => folders.LocalAppData,
            LogicalRootKind.UserProfile => folders.UserProfile,
            LogicalRootKind.ProgramData => folders.ProgramData,
            _ => path.RootKey ?? throw new InvalidOperationException("A custom or repository root needs a root key."),
        };

        return path.RelativePath.Length == 0 ? Paths.Normalize(root) : Paths.Combine(root, path.RelativePath);
    }

    private static void Walk(
        IMachine machine, MigrationArtifact artifact, PlannedRoot root, string directory, string relative, int depth,
        IReadOnlyList<ExclusionRule> rules, HashSet<string>? tracked, bool rulesSuspended, IReadOnlyList<string> protectedPaths,
        HashSet<string> capturedPaths, List<PlannedFile> files, List<AppliedExclusion> exclusions, List<PlanFinding> findings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            entries = [.. machine.FileSystem.EnumerateEntries(directory)];
        }
        catch (UnauthorizedAccessException)
        {
            findings.Add(new PlanFinding(FindingLevel.Warning, artifact.Id, $"{directory} could not be read (access denied) and will be skipped."));
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if (entries.Count == 0 && relative.Length > 0)
        {
            files.Add(new PlannedFile(root.Index, directory, relative, 0, DateTimeOffset.MinValue, true));
            return;
        }

        foreach (var entry in entries)
        {
            var childRelative = relative.Length == 0 ? entry.Name : $@"{relative}\{entry.Name}";

            if (protectedPaths.Any(p => Paths.IsUnder(entry.FullPath, p)))
            {
                findings.Add(new PlanFinding(FindingLevel.Information, artifact.Id, $"{entry.FullPath} is DevBR's own output, scratch or data folder and is not captured."));
                continue;
            }

            if (entry.IsReparsePoint)
            {
                findings.Add(new PlanFinding(FindingLevel.Information, artifact.Id, $"{entry.FullPath} is a link and is not followed."));
                continue;
            }

            if (entry.IsCloudPlaceholder)
            {
                findings.Add(new PlanFinding(FindingLevel.Warning, artifact.Id, $"{entry.FullPath} is cloud-only and was not downloaded.", "Make it available offline, then plan again."));
                continue;
            }

            if (entry.IsDirectory)
            {
                var rule = rulesSuspended ? null : rules.FirstOrDefault(r => r.Enabled && string.Equals(r.FolderName, entry.Name, StringComparison.OrdinalIgnoreCase));
                if (rule is not null)
                {
                    if (tracked is not null && GitIndexReader.ContainsTrackedContent(tracked, childRelative))
                    {
                        findings.Add(new PlanFinding(FindingLevel.Information, artifact.Id,
                            $"{entry.FullPath} matches the \"{rule.FolderName}\" exclusion but contains tracked files, so it is kept."));
                    }
                    else
                    {
                        var (count, bytes) = Measure(machine, entry.FullPath, cancellationToken);
                        exclusions.Add(new AppliedExclusion(entry.FullPath, rule.FolderName, count, bytes));
                        continue;
                    }
                }

                if (depth >= MaxDepth)
                {
                    findings.Add(new PlanFinding(FindingLevel.Warning, artifact.Id, $"{entry.FullPath} is nested too deeply and was skipped."));
                    continue;
                }

                Walk(machine, artifact, root, entry.FullPath, childRelative, depth + 1, rules, tracked, rulesSuspended, protectedPaths, capturedPaths, files, exclusions, findings, cancellationToken);
                continue;
            }

            // Overlapping selections: each file is captured once, by the first artifact that claims it.
            if (capturedPaths.Add(entry.FullPath))
            {
                files.Add(new PlannedFile(root.Index, entry.FullPath, childRelative, entry.Length, entry.LastWriteUtc, false));
            }
        }
    }

    /// <returns>True when the repository cannot be captured at all.</returns>
    private static bool CheckRepository(MigrationArtifact artifact, InventoryItem? item, HashSet<string> selected, Discovery.DiscoverySnapshot snapshot, List<PlanFinding> findings)
    {
        var p = item?.Properties ?? new Dictionary<string, string>();

        if (p.ContainsKey("locked"))
        {
            findings.Add(new PlanFinding(FindingLevel.Blocking, artifact.Id,
                $"{artifact.DisplayName}: a Git operation is in progress (lock file present), so the repository is not stable enough to capture.",
                "Finish or abort the Git operation, close tools using the repository, then plan again."));
            return true;
        }

        if (p.ContainsKey("gitDirMissing"))
        {
            findings.Add(new PlanFinding(FindingLevel.Blocking, artifact.Id, $"{artifact.DisplayName}: its git directory ({p.GetValueOrDefault("gitDir")}) is missing.",
                "Repair the repository (for example 'git worktree prune') or deselect it."));
            return true;
        }

        foreach (var dependency in artifact.Dependencies.Where(d => !selected.Contains(d)))
        {
            var name = snapshot.Artifacts.FirstOrDefault(a => a.Id == dependency)?.DisplayName ?? dependency;
            findings.Add(new PlanFinding(FindingLevel.Warning, artifact.Id,
                $"{artifact.DisplayName} keeps its Git data in {name}, which is not selected. Without it this backup of the repository is incomplete.",
                $"Also select {name}."));
        }

        if (p.GetValueOrDefault("objectAlternates") is { } alternates)
        {
            findings.Add(new PlanFinding(FindingLevel.Warning, artifact.Id,
                $"{artifact.DisplayName} borrows Git objects from {alternates.Replace('\n', ',')}; the backup is incomplete unless that repository is also captured.",
                "Select the repository that owns those objects, or run 'git repack -a -d' and remove the alternates file."));
        }

        if (p.ContainsKey("lfs"))
        {
            findings.Add(new PlanFinding(FindingLevel.Information, artifact.Id,
                $"{artifact.DisplayName} uses Git LFS. LFS objects present locally are included; any missing ones will need to be downloaded after restore."));
        }

        return false;
    }

    private static void Classify(MigrationArtifact artifact, List<string> encryption)
    {
        if (artifact.Sensitivity == Sensitivity.Credential)
        {
            encryption.Add($"{artifact.DisplayName} is a credential.");
        }
        else if (artifact.Sensitivity == Sensitivity.ContainsRecognizedSecrets && !CaptureTransforms.RedactsSecrets(artifact.Id))
        {
            encryption.Add($"{artifact.DisplayName} contains recognized secrets.");
        }
    }

    private static HashSet<string>? ReadTracked(IMachine machine, string repository, InventoryItem? item)
    {
        var gitDir = item?.Properties?.GetValueOrDefault("gitDir") ?? Paths.Combine(repository, ".git");
        var indexPath = Paths.Combine(gitDir, "index");
        if (!machine.FileSystem.FileExists(indexPath))
        {
            // A repository without an index (fresh clone with no checkout, bare) tracks nothing in its tree.
            return machine.FileSystem.DirectoryExists(gitDir) ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        }

        try
        {
            using var stream = machine.FileSystem.OpenRead(indexPath);
            return GitIndexReader.ReadTrackedPaths(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (long Files, long Bytes) Measure(IMachine machine, string directory, CancellationToken cancellationToken)
    {
        long files = 0, bytes = 0;
        var stack = new Stack<string>([directory]);
        while (stack.Count > 0 && files < 1_000_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in machine.FileSystem.EnumerateEntries(stack.Pop()))
                {
                    if (entry.IsReparsePoint)
                    {
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        stack.Push(entry.FullPath);
                    }
                    else
                    {
                        files++;
                        bytes += entry.Length;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }
        }

        return (files, bytes);
    }

    private static MigrationArtifact CustomArtifact(IMachine machine, string path)
    {
        var normalized = Paths.Normalize(path);
        return new MigrationArtifact(
            ItemIds.For("custom", Path.GetFileName(normalized), normalized),
            CustomOwner,
            ArtifactKind.CustomFiles,
            normalized,
            [Paths.ToLogical(machine.Folders, normalized)],
            Sensitivity.MayContainSecrets,
            BackupEligibility.Eligible,
            RestoreCapability.ExplicitFileRestore,
            [],
            false,
            "Added by you.",
            null,
            null,
            null);
    }

    internal static string Describe(long files, long bytes)
        => $"{files.ToString("N0", CultureInfo.InvariantCulture)} files, {bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes";
}
