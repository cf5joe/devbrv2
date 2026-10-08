using System.Collections.Concurrent;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DevBR.Application.Archive;
using DevBR.Application.Machine;
using DevBR.Application.Restore;
using DevBR.Backup;
using DevBR.Backup.Capture;
using DevBR.Discovery;
using DevBR.Discovery.Providers;
using DevBR.Discovery.Support;
using DevBR.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Restore.Execution;

/// <summary>
/// Carries out an approved restore. Preflight runs again first and the restore stops if the plan now has
/// effects the user did not approve. Payload is extracted to a private folder and verified before any
/// target is touched. Each artifact commits on its own: every change is journaled before it happens,
/// each target is checked against what preflight saw just before writing, and if one change of an
/// artifact fails, that artifact's earlier changes are undone and anything depending on it is blocked.
/// </summary>
public sealed class RestoreExecutor(RestorePlanner planner, IArchiveService archive, IRestoreJournal journal, IRollbackStore rollbackStore, ILogger<RestoreExecutor> logger)
{
    /// <summary>Jobs running in this process; recovery leaves them alone.</summary>
    internal static readonly ConcurrentDictionary<Guid, byte> ActiveJobs = new();

    private readonly IArchiveService archive = archive;
    private readonly IRestoreJournal journal = journal;
    private readonly IRollbackStore rollbackStore = rollbackStore;
    private readonly ILogger<RestoreExecutor> logger = logger;

    public static bool IsActive(Guid jobId) => ActiveJobs.ContainsKey(jobId);

    public async Task<RestoreRun> ExecuteAsync(RestoreExecutionRequest execution, IProgress<RestoreProgress>? progress, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        progress?.Report(new RestoreProgress(0, 0, "Checking this computer again"));
        var preflight = await planner.PreflightAsync(execution.Request, null, cancellationToken).ConfigureAwait(false);

        if (!execution.Approval.Covers(preflight))
        {
            logger.LogWarning("Restore not started: the plan changed since it was approved.");
            return new RestoreRun(Guid.Empty, RestoreRunOutcome.ApprovalOutdated,
                "This computer changed since you approved the plan, so the restore did not start. Review the updated plan and approve it again.",
                [], preflight, null, null, started, DateTimeOffset.UtcNow);
        }

        var request = execution.Request;
        var jobId = Guid.NewGuid();
        var summary = new RestoreJobSummary(preflight.Plan.PlanId, preflight.Plan.ApprovalHash, request.Overview.Manifest.ArchiveId, request.Overview.ArchivePath,
            request.Target.Info.ComputerName, [.. PlanApproval.EffectKeys(preflight).Order(StringComparer.Ordinal)]);
        journal.CreateJob(jobId, JobKinds.Restore, summary.ToJson());
        ActiveJobs[jobId] = 0;

        var run = new Run(this, execution, preflight, jobId, progress);
        try
        {
            await run.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelled inside extraction, the administrator prompt or an installation: still settle the job and write its report.
            run.Finish(cancelled: true);
        }
        finally
        {
            run.CleanUp();
            ActiveJobs.TryRemove(jobId, out _);
        }

        var reports = run.Reports();
        var cancelled = cancellationToken.IsCancellationRequested;
        var problems = reports.Any(r => r.Status is RestoreStatus.Failed or RestoreStatus.Blocked || r.Verification == VerificationLevel.VerificationFailed);
        var outcome = cancelled ? RestoreRunOutcome.Cancelled : problems ? RestoreRunOutcome.CompletedWithProblems : RestoreRunOutcome.Completed;
        var message = outcome switch
        {
            RestoreRunOutcome.Cancelled => "The restore was cancelled. Changes made before cancelling were kept; you can roll them back.",
            RestoreRunOutcome.CompletedWithProblems => "The restore finished, but some items need your attention.",
            _ => "The restore finished.",
        };

        var completed = DateTimeOffset.UtcNow;
        var result = new RestoreRun(jobId, outcome, message, reports, preflight, null, null, started, completed);
        string? html = null;
        string? json = null;
        try
        {
            (html, json) = RestoreReportWriter.Write(result, request.Overview, execution.ReportFolder);
            result = result with { ReportHtmlPath = html, ReportJsonPath = json };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not write the restore report.");
        }

        journal.UpdateJob(jobId, outcome switch
        {
            RestoreRunOutcome.Cancelled => JobStates.Cancelled,
            RestoreRunOutcome.CompletedWithProblems => JobStates.CompletedWithProblems,
            _ => JobStates.Completed,
        }, (summary with
        {
            Applied = result.Count(RestoreStatus.Applied),
            Failed = result.Count(RestoreStatus.Failed),
            Blocked = result.Count(RestoreStatus.Blocked),
            Skipped = result.Count(RestoreStatus.Skipped),
            ReportHtmlPath = html,
            ReportJsonPath = json,
        }).ToJson());

        logger.LogInformation("Restore job {JobId} finished: {Outcome}; {Applied} applied, {Failed} failed, {Blocked} blocked.", jobId, outcome,
            result.Count(RestoreStatus.Applied), result.Count(RestoreStatus.Failed), result.Count(RestoreStatus.Blocked));
        return result;
    }

    /// <summary>One execution's state.</summary>
    private sealed class Run(RestoreExecutor owner, RestoreExecutionRequest execution, RestorePreflight preflight, Guid jobId, IProgress<RestoreProgress>? progress)
    {
        private readonly Dictionary<string, OperationReport> _results = new(StringComparer.Ordinal);
        private readonly Dictionary<string, JournalIntent> _applied = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _staged = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _damagedArtifacts = new(StringComparer.Ordinal);
        private readonly PathRewriter _rewriter = new(preflight.Mapper);
        private string? _stagingFolder;
        private int _sequence;
        private List<(string Id, PlannedOperation Op, JournalIntent Intent)>? _committed;
        private int _done;
        private bool _environmentChanged;
        private PlannedOperation? _inFlight;

        private RestoreRequest Request => execution.Request;

        private IMachine Target => execution.Request.Target;

        private IMachineWriter Writer => execution.Writer;

        private int Total => preflight.Operations.Count;

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var runnable = new List<PlannedOperation>();
            foreach (var op in preflight.Operations)
            {
                var o = op.Operation;
                if (o.Action == RestoreAction.ManualStep)
                {
                    Report(op, RestoreStatus.Blocked, op.Detail, [$"{op.Title}, start it once, then run preflight again to restore what depends on it."]);
                }
                else if (o.Action == RestoreAction.Validate)
                {
                    continue;
                }
                else if (preflight.BlockedArtifacts.Contains(o.ArtifactId))
                {
                    Report(op, RestoreStatus.Blocked, "Preflight found a problem that blocks this item.", BlockingSteps(o.ArtifactId));
                }
                else if (!op.Enabled)
                {
                    Report(op, RestoreStatus.Skipped, op.Detail ?? "Held back.", []);
                }
                else if (o.Action == RestoreAction.Skip)
                {
                    Report(op, RestoreStatus.Skipped, op.Detail ?? "Nothing to do.", []);
                }
                else
                {
                    runnable.Add(op);
                }
            }

            progress?.Report(new RestoreProgress(_done, Total, "Extracting files from the backup"));
            await StageAsync(runnable, cancellationToken).ConfigureAwait(false);

            // 1. User-level changes, committed per artifact.
            foreach (var group in runnable.Where(IsUserChange).GroupBy(o => o.Operation.ArtifactId, StringComparer.Ordinal))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                CommitArtifact([.. group], cancellationToken);
            }

            // 2. Machine-wide environment changes: one administrator prompt for all of them.
            var elevated = runnable.Where(o => o.Operation.Privilege == PrivilegeRequirement.Elevated
                && o.Operation.Action is RestoreAction.SetEnvironmentVariable or RestoreAction.AppendPathEntry).ToList();
            if (elevated.Count > 0 && !cancellationToken.IsCancellationRequested)
            {
                await ApplyElevatedAsync(elevated, cancellationToken).ConfigureAwait(false);
            }

            // 3. Installations (cannot be rolled back).
            foreach (var op in runnable.Where(o => o.Operation.Action == RestoreAction.InstallDependency))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await InstallAsync(op, cancellationToken).ConfigureAwait(false);
            }

            Finish(cancellationToken.IsCancellationRequested);
        }

        /// <summary>Reports what did not run, announces environment changes and verifies what was applied.</summary>
        public void Finish(bool cancelled)
        {
            if (_inFlight is { } interrupted && !_results.ContainsKey(interrupted.Operation.Id))
            {
                Report(interrupted, RestoreStatus.Failed, "The restore was cancelled while this change was in progress, so DevBR could not confirm whether it was made.",
                    ["Check this item yourself, or roll the restore back from Recent restores."]);
            }

            _inFlight = null;
            foreach (var op in preflight.Operations.Where(o => o.Operation.Action != RestoreAction.Validate && !_results.ContainsKey(o.Operation.Id)))
            {
                Report(op, RestoreStatus.Skipped, cancelled ? "Not started because the restore was cancelled." : op.Detail ?? "Not started.", []);
            }

            if (_environmentChanged)
            {
                Writer.BroadcastEnvironmentChange();
            }

            progress?.Report(new RestoreProgress(_done, Total, "Checking the restored items"));
            Validate();
        }

        private static bool IsUserChange(PlannedOperation op)
            => op.Operation.Action is not RestoreAction.InstallDependency
               && !(op.Operation.Privilege == PrivilegeRequirement.Elevated && op.Operation.Action is RestoreAction.SetEnvironmentVariable or RestoreAction.AppendPathEntry);

        public IReadOnlyList<OperationReport> Reports()
            => [.. preflight.Operations.Select(o => _results.GetValueOrDefault(o.Operation.Id)).OfType<OperationReport>()];

        public void CleanUp()
        {
            if (_stagingFolder is not null)
            {
                try
                {
                    Directory.Delete(_stagingFolder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    owner.logger.LogWarning(ex, "Could not remove the restore staging folder.");
                }
            }
        }

        // --- Staging ------------------------------------------------------------------------------------

        private async Task StageAsync(List<PlannedOperation> runnable, CancellationToken cancellationToken)
        {
            var wanted = new Dictionary<string, (string ArtifactId, string? Sha256)>(StringComparer.OrdinalIgnoreCase);
            foreach (var op in runnable)
            {
                if (op.Operation.Action is RestoreAction.CreateFile or RestoreAction.ReplaceFile or RestoreAction.MergeStructuredSettings or RestoreAction.RestoreAlongside
                    && op.ArchivePath is not null && op.Sha256 is not null)
                {
                    wanted[op.ArchivePath] = (op.Operation.ArtifactId, op.Sha256);
                }
                else if (op.Operation.Action == RestoreAction.RestoreRepository)
                {
                    foreach (var entry in RepositoryEntries(op.Operation.ArtifactId).Where(e => e.EntryType == ArchiveEntryType.File))
                    {
                        wanted[entry.ArchivePath] = (op.Operation.ArtifactId, entry.Sha256);
                    }
                }
            }

            if (wanted.Count == 0)
            {
                return;
            }

            _stagingFolder = Path.Combine(Request.WorkFolder, $"restore-{jobId:N}");
            CreatePrivateDirectory(_stagingFolder);
            var result = await owner.archive.ExtractSelectedAsync(new ArchiveExtractRequest(Request.Overview.ArchivePath, Request.Password, _stagingFolder, [.. wanted.Keys]),
                null, cancellationToken).ConfigureAwait(false);

            var byPath = result.Files.ToDictionary(f => f.ArchivePath, StringComparer.OrdinalIgnoreCase);
            foreach (var (archivePath, (artifactId, sha)) in wanted)
            {
                if (byPath.TryGetValue(ArchivePathValidator.Normalize(archivePath), out var file) && string.Equals(file.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                {
                    _staged[archivePath] = file.DestinationPath;
                }
                else
                {
                    _damagedArtifacts.Add(artifactId);
                }
            }
        }

        private IReadOnlyList<ArchiveEntry> RepositoryEntries(string artifactId)
        {
            var key = Request.Overview.Artifacts.First(a => a.Record.Artifact.Id == artifactId).Record.Key;
            return BackupReader.ReadEntries(Request.Overview, key, 0, int.MaxValue);
        }

        // --- Per-artifact commit -------------------------------------------------------------------------

        private void CommitArtifact(List<PlannedOperation> operations, CancellationToken cancellationToken)
        {
            var artifactId = operations[0].Operation.ArtifactId;
            if (_damagedArtifacts.Contains(artifactId))
            {
                foreach (var op in operations)
                {
                    Report(op, RestoreStatus.Failed, "Its content in the backup did not match the recorded hash, so nothing was written.",
                        ["Copy the backup again from its source and restore this item again."]);
                }

                return;
            }

            // Folders first (shortest first), then everything else in plan order.
            var ordered = operations.Where(IsFolder).OrderBy(o => o.Operation.Target.Length).Concat(operations.Where(o => !IsFolder(o))).ToList();
            _committed = [];
            string? failure = null;
            string? failedId = null;

            foreach (var op in ordered)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (failure is not null)
                {
                    Report(op, RestoreStatus.Blocked, $"Not attempted: {failure}", ["Fix the problem above, then run preflight and restore this item again."]);
                    continue;
                }

                if (op.Operation.DependsOn.FirstOrDefault(d => _results.TryGetValue(d, out var r) && r.Status is RestoreStatus.Failed or RestoreStatus.Blocked) is { } dependency)
                {
                    Report(op, RestoreStatus.Blocked, $"Waiting for {dependency}.", BlockingSteps(artifactId));
                    continue;
                }

                progress?.Report(new RestoreProgress(_done, Total, op.Title));
                try
                {
                    var intent = Apply(op);
                    if (intent is not null)
                    {
                        _applied[op.Operation.Id] = intent;
                    }

                    Report(op, RestoreStatus.Applied, intent is null ? "Already in place." : null, [], rollback: intent?.Reversible ?? false);
                }
                catch (Exception ex) when (ex is TargetChangedException or IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    owner.logger.LogWarning("Restore operation {OperationId} failed: {Message}", op.Operation.Id, ex.Message);
                    failure = ex is TargetChangedException ? ex.Message : $"{op.Title} failed: {ex.Message}";
                    failedId = op.Operation.Id;
                    Report(op, RestoreStatus.Failed, failure,
                        ex is TargetChangedException ? ["Run preflight again to see the current state, then restore this item again."] : ["Close programs that may be using the file, then try again."]);
                }
            }

            if (failure is null || _committed.Count == 0)
            {
                return;
            }

            // Undo this artifact's changes (including any part of the failed one) so it is not left half-restored.
            var undo = new RestoreUndo(owner.journal, owner.rollbackStore, Target, Writer, null);
            foreach (var (id, op, intent) in Enumerable.Reverse(_committed))
            {
                var (ok, message) = undo.Undo(jobId, id, intent, ref _sequence);
                _applied.Remove(id);
                if (id == failedId)
                {
                    if (!ok)
                    {
                        Report(op, RestoreStatus.Failed, $"{failure} Part of it could not be undone: {message}", [$"Check {op.Operation.Target} yourself."]);
                    }
                }
                else if (id == op.Operation.Id)
                {
                    Report(op, RestoreStatus.Failed, ok ? $"Undone because another change to this item failed ({failure})." : $"Could not be undone: {message}",
                        ok ? ["Fix the problem, then run preflight and restore this item again."] : [$"Check {op.Operation.Target} yourself."]);
                }
            }
        }

        /// <summary>Creates missing parent folders one by one, journaling each so rollback removes them again.</summary>
        private void CreateParents(PlannedOperation op, string? folder)
        {
            var missing = new Stack<string>();
            for (var dir = folder; !string.IsNullOrEmpty(dir) && !Target.FileSystem.DirectoryExists(dir); dir = Path.GetDirectoryName(dir))
            {
                missing.Push(dir);
            }

            while (missing.TryPop(out var dir))
            {
                if (Target.FileSystem.FileExists(dir))
                {
                    throw new TargetChangedException($"{dir} is a file; a folder was expected there.");
                }

                Commit($"{op.Operation.Id}#folder:{dir}", op, new JournalIntent("directory", op.Operation.ArtifactId, $"Create folder {dir}", dir, "User", "dir-absent", "dir-present",
                    null, false, false, true), () => Writer.CreateDirectory(dir));
            }
        }

        private static bool IsFolder(PlannedOperation op) => op.Operation.Action == RestoreAction.CreateFile && op.Operation.ExpectedTargetState == "directory";

        /// <returns>The journaled intent, or null when the target was already in the desired state.</returns>
        private JournalIntent? Apply(PlannedOperation op)
        {
            var o = op.Operation;
            switch (o.Action)
            {
                case RestoreAction.CreateFile when o.ExpectedTargetState == "directory":
                    if (Target.FileSystem.FileExists(o.Target))
                    {
                        throw new TargetChangedException($"{o.Target} is now a file; a folder was expected.");
                    }

                    if (Target.FileSystem.DirectoryExists(o.Target))
                    {
                        return null;
                    }

                    CreateParents(op, Path.GetDirectoryName(o.Target));
                    return Commit(op, new JournalIntent("directory", o.ArtifactId, op.Title, o.Target, "User", "dir-absent", "dir-present", null, false, false, true),
                        () => Writer.CreateDirectory(o.Target));

                case RestoreAction.CreateFile or RestoreAction.RestoreAlongside:
                {
                    var state = TargetProbe.FileState(Target, o.Target);
                    if (state != "absent")
                    {
                        throw new TargetChangedException($"{o.Target} now exists; it was not there when the plan was made. Nothing was written.");
                    }

                    var content = BackupContent(op);
                    CreateParents(op, Path.GetDirectoryName(o.Target));
                    return Commit(op, new JournalIntent("file", o.ArtifactId, op.Title, o.Target, "User", "absent", Journal.Sha(content), null, false, false, true),
                        () => Writer.WriteFileAtomic(o.Target, content));
                }

                case RestoreAction.ReplaceFile:
                {
                    var current = ExpectFile(o);
                    var content = BackupContent(op);
                    var copy = SaveCopy(current);
                    return Commit(op, new JournalIntent("file", o.ArtifactId, op.Title, o.Target, "User", o.ExpectedTargetState, Journal.Sha(content), copy, false, false, true),
                        () => Writer.WriteFileAtomic(o.Target, content));
                }

                case RestoreAction.MergeStructuredSettings:
                {
                    var current = ExpectFile(o);
                    var profile = MergeProfile.For(o.ArtifactId, Path.GetFileName(o.Target)) ?? throw new InvalidDataException("No merge rules exist for this file.");
                    var backupNode = JsonMerger.Parse(Staged(op)) ?? throw new InvalidDataException("The backed-up file is not valid JSON.");
                    JsonMerger.RewritePaths(backupNode, profile, _rewriter);
                    var targetNode = JsonMerger.Parse(current) ?? throw new TargetChangedException($"{o.Target} is no longer valid JSON; nothing was merged.");
                    var (merged, _) = JsonMerger.Merge(targetNode, backupNode, profile, preferBackup: o.ConflictDecision == ConflictDecision.UseBackup);
                    var content = JsoncEditor.Apply(current, merged) ?? JsoncEditor.Serialize(merged);
                    var copy = SaveCopy(current);
                    return Commit(op, new JournalIntent("file", o.ArtifactId, op.Title, o.Target, "User", o.ExpectedTargetState, Journal.Sha(content), copy, false, false, true),
                        () => Writer.WriteFileAtomic(o.Target, content));
                }

                case RestoreAction.RestoreRepository:
                    return RestoreRepository(op);

                case RestoreAction.SetEnvironmentVariable:
                {
                    var (scope, name) = ParseVariable(o.Target);
                    var value = op.Value ?? throw new InvalidDataException("The planned value is missing.");
                    var current = TargetProbe.ReadVariable(Target, scope, name);
                    CheckVariable(o, current?.AsString());
                    var copy = current?.AsString() is { } previous ? owner.rollbackStore.Save(jobId, new MemoryStream(Journal.Utf8(previous))) : null;
                    var intent = new JournalIntent("env", o.ArtifactId, op.Title, name, scope, current is null ? "absent" : Journal.Sha(current.AsString()!), Journal.Sha(value), copy,
                        op.Expandable, current?.Kind == RegistryValueKind.ExpandString, true);
                    _environmentChanged = true;
                    return Commit(op, intent, () => Writer.SetUserEnvironmentVariable(name, value, op.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String));
                }

                case RestoreAction.AppendPathEntry:
                {
                    var entry = op.Value ?? o.ExpectedTargetState ?? throw new InvalidDataException("The planned entry is missing.");
                    var current = TargetProbe.ReadVariable(Target, "User", "Path");
                    if (TargetProbe.HasPathEntry(current?.AsString(), entry))
                    {
                        return null;
                    }

                    var kind = current?.Kind ?? RegistryValueKind.ExpandString;
                    var intent = new JournalIntent("path", o.ArtifactId, op.Title, "Path", "User", current is null ? "absent" : "present", entry, null,
                        kind == RegistryValueKind.ExpandString, kind == RegistryValueKind.ExpandString, true);
                    _environmentChanged = true;
                    return Commit(op, intent, () => Writer.SetUserEnvironmentVariable("Path", RestoreEffects.AppendPathEntry(current?.AsString(), entry), kind));
                }

                default:
                    throw new InvalidDataException($"{o.Action} is not supported here.");
            }
        }

        private JournalIntent Commit(PlannedOperation op, JournalIntent intent, Action change) => Commit(op.Operation.Id, op, intent, change);

        private JournalIntent Commit(string id, PlannedOperation op, JournalIntent intent, Action change)
        {
            owner.journal.RecordIntent(jobId, id, ++_sequence, intent.ToJson());

            // Listed before the change runs: if it fails partway (a repository), undo still looks at what it left behind.
            _committed?.Add((id, op, intent));
            try
            {
                change();
            }
            catch (Exception ex)
            {
                owner.journal.RecordOutcome(jobId, id, new JournalOutcome(Journal.Failed, ex.Message).ToJson());
                throw;
            }

            owner.journal.RecordOutcome(jobId, id, new JournalOutcome(Journal.Applied, null).ToJson());
            return intent;
        }

        private byte[] ExpectFile(RestoreOperation o)
        {
            var state = TargetProbe.FileState(Target, o.Target);
            if (!string.Equals(state, o.ExpectedTargetState, StringComparison.OrdinalIgnoreCase))
            {
                throw new TargetChangedException($"{o.Target} changed after the plan was made. Nothing was written to it.");
            }

            return TargetProbe.ReadAll(Target, o.Target);
        }

        private void CheckVariable(RestoreOperation o, string? current)
        {
            var expected = o.ExpectedTargetState;
            var matches = expected == "absent" ? current is null
                : expected?.StartsWith("value:", StringComparison.Ordinal) == true && current is not null && RestoreEffects.Sha256(current)[..16] == expected["value:".Length..];
            if (!matches)
            {
                throw new TargetChangedException($"{o.Target["ENV:".Length..]} changed after the plan was made. It was not changed.");
            }
        }

        private static (string Scope, string Name) ParseVariable(string target)
        {
            // ENV:<Scope>:<Name>
            var parts = target.Split(':', 3);
            return parts.Length == 3 ? (parts[1], parts[2]) : throw new InvalidDataException($"Unexpected environment target {target}.");
        }

        private string SaveCopy(byte[] current)
        {
            using var stream = new MemoryStream(current, writable: false);
            return owner.rollbackStore.Save(jobId, stream);
        }

        private byte[] Staged(PlannedOperation op)
            => op.ArchivePath is not null && _staged.TryGetValue(op.ArchivePath, out var path)
                ? File.ReadAllBytes(path)
                : throw new InvalidDataException("The file was not extracted from the backup.");

        /// <summary>The backed-up file with declared path fields rewritten for this computer.</summary>
        private byte[] BackupContent(PlannedOperation op)
        {
            var bytes = Staged(op);
            var profile = MergeProfile.For(op.Operation.ArtifactId, Path.GetFileName(op.Operation.Target));
            if (profile is null || JsonMerger.Parse(bytes) is not { } node || JsonMerger.RewritePaths(node, profile, _rewriter).Count == 0)
            {
                return bytes;
            }

            // Keep comments and layout: apply only the rewritten fields.
            return JsoncEditor.Apply(bytes, node) ?? JsoncEditor.Serialize(node);
        }

        // --- Repositories --------------------------------------------------------------------------------

        private JournalIntent RestoreRepository(PlannedOperation op)
        {
            var o = op.Operation;
            var destination = o.Target;
            if (Target.FileSystem.FileExists(destination))
            {
                throw new TargetChangedException($"{destination} is now a file. Nothing was written.");
            }

            var existed = Target.FileSystem.DirectoryExists(destination);
            if (existed && !TargetProbe.DirectoryIsEmpty(Target, destination))
            {
                throw new TargetChangedException($"{destination} is no longer empty. Nothing was written.");
            }

            var entries = RepositoryEntries(o.ArtifactId).Where(e => e.RelativePath.Length > 0).ToList();
            var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Where(e => e.EntryType == ArchiveEntryType.File && RestorePlanner.IsGitPointer(e.RelativePath)))
            {
                if (_staged.TryGetValue(entry.ArchivePath, out var staged) && RewritePointer(entry.RelativePath, File.ReadAllBytes(staged)) is { } rewritten)
                {
                    contents[entry.ArchivePath] = rewritten;
                }
            }

            var manifest = new RepositoryManifest([.. entries.Select(e => new RepositoryManifestEntry(e.RelativePath, e.EntryType == ArchiveEntryType.Directory,
                e.EntryType == ArchiveEntryType.Directory ? null : contents.TryGetValue(e.ArchivePath, out var c) ? Journal.Sha(c) : "sha256:" + e.Sha256!.ToLowerInvariant()))]);
            string copy;
            using (var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, Journal.Json)))
            {
                copy = owner.rollbackStore.Save(jobId, stream);
            }

            CreateParents(op, Path.GetDirectoryName(destination));
            var intent = new JournalIntent("repository", o.ArtifactId, op.Title, destination, "User", existed ? "dir-empty" : "dir-absent", "repository", copy, false, false, true);
            return Commit(op, intent, () =>
            {
                Writer.CreateDirectory(destination);
                foreach (var entry in entries.OrderBy(e => e.EntryType == ArchiveEntryType.Directory ? 0 : 1).ThenBy(e => e.RelativePath.Length))
                {
                    var path = Path.Combine(destination, entry.RelativePath);
                    if (entry.EntryType == ArchiveEntryType.Directory)
                    {
                        Writer.CreateDirectory(path);
                    }
                    else if (contents.TryGetValue(entry.ArchivePath, out var rewritten))
                    {
                        Writer.WriteFileAtomic(path, rewritten);
                    }
                    else if (_staged.TryGetValue(entry.ArchivePath, out var staged))
                    {
                        using var input = File.OpenRead(staged);
                        Writer.WriteFileAtomic(path, input);
                    }
                    else
                    {
                        throw new InvalidDataException($"{entry.RelativePath} was not extracted from the backup.");
                    }
                }
            });
        }

        /// <summary>Rewrites an absolute git directory reference (".git" file or worktree "gitdir"); null when unchanged.</summary>
        private byte[]? RewritePointer(string relativePath, byte[] content)
        {
            var text = Encoding.UTF8.GetString(content);
            var trimmed = text.Trim();
            var prefixed = trimmed.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase);
            var pointer = prefixed ? trimmed["gitdir:".Length..].Trim() : relativePath.EndsWith(@"\gitdir", StringComparison.OrdinalIgnoreCase) ? trimmed : null;
            if (pointer is null || !(pointer.Length > 2 && pointer[1] == ':'))
            {
                return null;
            }

            var (after, outcome) = _rewriter.Rewrite(pointer);
            if (outcome != RewriteOutcome.Rewritten)
            {
                return null;
            }

            var newline = text.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : text.EndsWith('\n') ? "\n" : string.Empty;
            return Encoding.UTF8.GetBytes((prefixed ? "gitdir: " + after : after) + newline);
        }

        // --- Machine environment -------------------------------------------------------------------------

        private async Task ApplyElevatedAsync(List<PlannedOperation> operations, CancellationToken cancellationToken)
        {
            progress?.Report(new RestoreProgress(_done, Total, "Waiting for administrator approval"));
            var session = await execution.Elevation.RequestAsync(cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                foreach (var op in operations)
                {
                    Report(op, RestoreStatus.Blocked, "Administrator approval was declined, so machine-wide settings were not changed.",
                        ["Restore again and approve the Windows prompt, or set this yourself in System Properties > Environment Variables."]);
                }

                return;
            }

            await using (session.ConfigureAwait(false))
            {
                foreach (var op in operations)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var o = op.Operation;
                    progress?.Report(new RestoreProgress(_done, Total, op.Title));
                    try
                    {
                        JournalIntent? intent;
                        EnvironmentChange change;
                        if (o.Action == RestoreAction.AppendPathEntry)
                        {
                            var entry = op.Value ?? o.ExpectedTargetState!;
                            var current = TargetProbe.ReadVariable(Target, "Machine", "Path");
                            if (TargetProbe.HasPathEntry(current?.AsString(), entry))
                            {
                                Report(op, RestoreStatus.Applied, "Already in place.", []);
                                continue;
                            }

                            var expandable = (current?.Kind ?? RegistryValueKind.ExpandString) == RegistryValueKind.ExpandString;
                            change = new EnvironmentChange("Path", current?.AsString(), RestoreEffects.AppendPathEntry(current?.AsString(), entry), expandable);
                            intent = new JournalIntent("path", o.ArtifactId, op.Title, "Path", "Machine", current is null ? "absent" : "present", entry, null, expandable, expandable, true);
                        }
                        else
                        {
                            var (_, name) = ParseVariable(o.Target);
                            var current = TargetProbe.ReadVariable(Target, "Machine", name);
                            CheckVariable(o, current?.AsString());
                            var copy = current?.AsString() is { } previous ? owner.rollbackStore.Save(jobId, new MemoryStream(Journal.Utf8(previous))) : null;
                            change = new EnvironmentChange(name, current?.AsString(), op.Value!, op.Expandable);
                            intent = new JournalIntent("env", o.ArtifactId, op.Title, name, "Machine", current is null ? "absent" : Journal.Sha(current.AsString()!), Journal.Sha(op.Value!),
                                copy, op.Expandable, current?.Kind == RegistryValueKind.ExpandString, true);
                        }

                        owner.journal.RecordIntent(jobId, o.Id, ++_sequence, intent.ToJson());
                        _inFlight = op;
                        try
                        {
                            await session.ApplyMachineEnvironmentAsync(jobId, preflight.Plan.ApprovalHash, [change], cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _inFlight = null;
                            owner.journal.RecordOutcome(jobId, o.Id, new JournalOutcome(Journal.Failed, ex.Message).ToJson());
                            throw;
                        }

                        _inFlight = null;
                        owner.journal.RecordOutcome(jobId, o.Id, new JournalOutcome(Journal.Applied, null).ToJson());
                        _applied[o.Id] = intent;
                        _environmentChanged = true;
                        Report(op, RestoreStatus.Applied, null, [], rollback: true);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        owner.logger.LogWarning("Machine environment change {OperationId} failed: {Message}", o.Id, ex.Message);
                        Report(op, RestoreStatus.Failed, ex.Message, ["Run preflight again, or set this yourself in System Properties > Environment Variables."]);
                    }
                }
            }
        }

        // --- Installations -------------------------------------------------------------------------------

        private async Task InstallAsync(PlannedOperation op, CancellationToken cancellationToken)
        {
            var o = op.Operation;
            if (o.DependsOn.FirstOrDefault(d => _results.TryGetValue(d, out var r) && r.Status is RestoreStatus.Failed or RestoreStatus.Blocked) is { } dependency)
            {
                Report(op, RestoreStatus.Blocked, $"Waiting for {dependency}.", BlockingSteps(o.ArtifactId));
                return;
            }

            if (op.Recipe is not { } recipe)
            {
                Report(op, RestoreStatus.Skipped, "No approved installation recipe.", []);
                return;
            }

            progress?.Report(new RestoreProgress(_done, Total, op.Title));
            var install = new PackageInstallRequest(recipe.Kind == RecipeKind.WinGet ? PackageKind.WinGet : PackageKind.EditorExtension, recipe.PackageId, recipe.Version, recipe.Source);
            owner.journal.RecordIntent(jobId, o.Id, ++_sequence,
                new JournalIntent("install", o.ArtifactId, op.Title, recipe.Preview, o.Privilege.ToString(), null, null, null, false, false, false).ToJson());

            PackageInstallOutcome outcome;
            _inFlight = op;
            try
            {
                outcome = await execution.Installer.InstallAsync(install, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                outcome = new PackageInstallOutcome(false, -1, ex.Message);
            }

            _inFlight = null;
            owner.journal.RecordOutcome(jobId, o.Id, new JournalOutcome(outcome.Succeeded ? Journal.Applied : Journal.Failed, $"exit code {outcome.ExitCode}").ToJson());
            if (outcome.Succeeded)
            {
                _applied[o.Id] = new JournalIntent("install", o.ArtifactId, op.Title, recipe.Id, o.Privilege.ToString(), null, null, null, false, false, false);
                Report(op, RestoreStatus.Applied, $"{recipe.Preview} finished. Installations cannot be rolled back; uninstall it yourself if needed.", []);
            }
            else
            {
                var tail = outcome.Output.Length > 400 ? "…" + outcome.Output[^400..] : outcome.Output;
                Report(op, RestoreStatus.Failed, $"{recipe.Preview} failed (exit code {outcome.ExitCode}). {tail}".Trim(), [$"Run '{recipe.Preview}' yourself to see the full output."]);
            }
        }

        // --- Validation ----------------------------------------------------------------------------------

        private void Validate()
        {
            IReadOnlyList<Domain.InventoryItem>? installed = null;
            foreach (var (id, intent) in _applied)
            {
                var report = _results[id];
                VerificationLevel level;
                string? note = null;
                switch (intent.Kind)
                {
                    case "file":
                        var state = TargetProbe.FileState(Target, intent.Target);
                        level = state != intent.After ? VerificationLevel.VerificationFailed
                            : intent.Target.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && JsonMerger.Parse(TargetProbe.ReadAll(Target, intent.Target)) is null
                                ? VerificationLevel.VerificationFailed
                                : VerificationLevel.ConfigurationApplied;
                        note = level == VerificationLevel.VerificationFailed ? "The file on disk does not match what was written; another program may have changed it." : null;
                        break;
                    case "directory":
                        level = Target.FileSystem.DirectoryExists(intent.Target) ? VerificationLevel.ConfigurationApplied : VerificationLevel.VerificationFailed;
                        break;
                    case "repository":
                        level = ValidateRepository(intent.Target);
                        note = level == VerificationLevel.VerificationFailed ? "Git data in the restored folder could not be read." : null;
                        break;
                    case "env":
                        level = TargetProbe.VariableState(Target, intent.Scope, intent.Target) == intent.After ? VerificationLevel.ConfigurationApplied : VerificationLevel.VerificationFailed;
                        break;
                    case "path":
                        level = TargetProbe.HasPathEntry(TargetProbe.ReadVariable(Target, intent.Scope, "Path")?.AsString(), intent.After!)
                            ? VerificationLevel.ConfigurationApplied : VerificationLevel.VerificationFailed;
                        break;
                    case "install":
                        installed ??= InstalledItems();
                        level = IsInstalled(id, intent, installed) ? VerificationLevel.FunctionallyVerified : VerificationLevel.ConfigurationApplied;
                        note = level == VerificationLevel.FunctionallyVerified ? null : "The installer finished, but DevBR could not find the result yet. A restart or new terminal may be needed.";
                        break;
                    default:
                        level = VerificationLevel.NotVerified;
                        break;
                }

                _results[id] = report with
                {
                    Verification = level,
                    Detail = note is null ? report.Detail : string.Join(' ', new[] { report.Detail, note }.Where(s => !string.IsNullOrEmpty(s))),
                };
            }
        }

        private VerificationLevel ValidateRepository(string destination)
        {
            var git = Path.Combine(destination, ".git");
            if (Target.FileSystem.FileExists(git))
            {
                // A linked worktree or submodule: its pointer file is the whole of its local Git data.
                return Target.FileSystem.ReadText(git, 4096)?.TrimStart().StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) == true
                    ? VerificationLevel.ConfigurationApplied
                    : VerificationLevel.VerificationFailed;
            }

            if (!Target.FileSystem.FileExists(Path.Combine(git, "HEAD")))
            {
                return VerificationLevel.VerificationFailed;
            }

            var index = Path.Combine(git, "index");
            if (!Target.FileSystem.FileExists(index))
            {
                return VerificationLevel.ConfigurationApplied;
            }

            try
            {
                using var stream = Target.FileSystem.OpenRead(index);
                return GitIndexReader.ReadTrackedPaths(stream) is not null ? VerificationLevel.FunctionallyVerified : VerificationLevel.VerificationFailed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return VerificationLevel.VerificationFailed;
            }
        }

        private IReadOnlyList<Domain.InventoryItem> InstalledItems()
        {
            var providers = DiscoveryEngine.DefaultProviders().Where(p => p is not FileSystemScanProvider).ToList();
            var snapshot = new DiscoveryEngine(providers, NullLogger<DiscoveryEngine>.Instance)
                .RunAsync(Target, DiscoveryOptions.Default with { ScanFixedDrives = false }, null, CancellationToken.None).GetAwaiter().GetResult();
            return snapshot.Items;
        }

        private static bool IsInstalled(string operationId, JournalIntent intent, IReadOnlyList<Domain.InventoryItem> items)
        {
            if (operationId.StartsWith("runtime:", StringComparison.Ordinal))
            {
                var toolId = operationId["runtime:".Length..];
                return items.Any(i => i.ToolId == toolId && i.Category != Domain.InventoryCategory.Extension);
            }

            // ext:<editor>:<id>
            var parts = intent.Target.Split(':', 3);
            return parts.Length == 3 && items.Any(i => i.Category == Domain.InventoryCategory.Extension && i.ToolId == parts[1]
                && string.Equals(i.Name, parts[2], StringComparison.OrdinalIgnoreCase));
        }

        // --- Results -------------------------------------------------------------------------------------

        private void Report(PlannedOperation op, RestoreStatus status, string? detail, IReadOnlyList<string> nextSteps, bool rollback = false)
        {
            if (!_results.ContainsKey(op.Operation.Id))
            {
                _done++;
            }

            var name = Request.Overview.Artifacts.FirstOrDefault(a => a.Record.Artifact.Id == op.Operation.ArtifactId)?.Record.Artifact.DisplayName ?? op.Operation.ArtifactId;
            _results[op.Operation.Id] = new OperationReport(op.Operation.Id, op.Operation.ArtifactId, name, op.Title, op.Operation.Action, status,
                VerificationLevel.NotVerified, rollback, detail, nextSteps);
            progress?.Report(new RestoreProgress(_done, Total, op.Title));
        }

        private List<string> BlockingSteps(string artifactId)
            => [.. preflight.Findings.Where(f => f.Severity == FindingSeverity.Blocking && f.AffectedArtifactIds.Contains(artifactId)).SelectMany(f => f.NextSteps).Distinct()];

        private static void CreatePrivateDirectory(string path)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.CreateDirectory(path);
        }
    }
}
