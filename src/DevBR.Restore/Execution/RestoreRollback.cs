using DevBR.Application.Machine;
using DevBR.Application.Restore;
using Microsoft.Extensions.Logging;

namespace DevBR.Restore.Execution;

public sealed record RollbackItem(string OperationId, string Title, string Message);

public sealed record RollbackRun(Guid JobId, IReadOnlyList<RollbackItem> Undone, IReadOnlyList<RollbackItem> Failed, IReadOnlyList<RollbackItem> NotReversible)
{
    public bool Complete => Failed.Count == 0;
}

public enum RecoveryClassification
{
    Applied,
    NotApplied,

    /// <summary>The target matches neither the state before nor the state after the change. It is never redone automatically.</summary>
    Uncertain,
}

public sealed record RecoveredOperation(string OperationId, string Title, RecoveryClassification Classification);

/// <summary>Rolls back a restore job in reverse order, and inspects jobs that were interrupted.</summary>
public sealed class RestoreRollbackService(IRestoreJournal journal, IRollbackStore store, ILogger<RestoreRollbackService> logger)
{
    public async Task<RollbackRun> RollbackAsync(Guid jobId, IMachine target, IMachineWriter writer, IElevationProvider elevation, CancellationToken cancellationToken)
    {
        var job = journal.GetJob(jobId) ?? throw new InvalidOperationException("That restore job is not in the journal.");
        var summary = RestoreJobSummary.FromJson(job.Summary);
        var records = journal.Read(jobId);
        var undone = records.Where(r => r.OperationId.StartsWith(Journal.UndoPrefix, StringComparison.Ordinal) && JournalOutcome.FromJson(r.Outcome)?.Status == Journal.Undone)
            .Select(r => r.OperationId[Journal.UndoPrefix.Length..]).ToHashSet(StringComparer.Ordinal);
        var sequence = records.Count == 0 ? 0 : records.Max(r => r.Sequence);

        var done = new List<RollbackItem>();
        var failed = new List<RollbackItem>();
        var irreversible = new List<RollbackItem>();
        var undo = new RestoreUndo(journal, store, target, writer, () => elevation.RequestAsync(cancellationToken));

        try
        {
            foreach (var record in records.Where(r => !r.OperationId.StartsWith(Journal.UndoPrefix, StringComparison.Ordinal)).OrderByDescending(r => r.Sequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var intent = JournalIntent.FromJson(record.Intent);
                var outcome = JournalOutcome.FromJson(record.Outcome);
                if (intent is null || undone.Contains(record.OperationId))
                {
                    continue;
                }

                // Changes that never happened need nothing; interrupted or uncertain ones are checked against the target.
                if (outcome?.Status is Journal.Failed or Journal.RecoveredNotApplied)
                {
                    continue;
                }

                if (!intent.Reversible)
                {
                    if (outcome?.Status is Journal.Applied or Journal.RecoveredApplied or Journal.Uncertain or null)
                    {
                        irreversible.Add(new RollbackItem(record.OperationId, intent.Title, "Installations cannot be rolled back; uninstall it yourself if you no longer want it."));
                    }

                    continue;
                }

                var (ok, message) = await undo.UndoAsync(jobId, record.OperationId, intent, ++sequence, summary?.ApprovalHash, cancellationToken).ConfigureAwait(false);
                (ok ? done : failed).Add(new RollbackItem(record.OperationId, intent.Title, message));
            }
        }
        finally
        {
            await undo.DisposeSessionAsync().ConfigureAwait(false);
            if (undo.EnvironmentChanged)
            {
                writer.BroadcastEnvironmentChange();
            }
        }

        journal.UpdateJob(jobId, failed.Count == 0 ? JobStates.RolledBack : JobStates.PartiallyRolledBack);
        if (failed.Count == 0)
        {
            store.DeleteJob(jobId);
        }

        logger.LogInformation("Rollback of restore job {JobId}: {Undone} undone, {Failed} kept, {Irreversible} not reversible.", jobId, done.Count, failed.Count, irreversible.Count);
        return new RollbackRun(jobId, done, failed, irreversible);
    }

    /// <summary>Restore jobs left running by a process that is gone (a crash or power loss).</summary>
    public IReadOnlyList<JournalJob> FindInterrupted()
        => [.. journal.ListJobs(JobKinds.Restore, 100).Where(j => j.State == JobStates.Running && !RestoreExecutor.IsActive(j.JobId))];

    /// <summary>
    /// Settles every operation of an interrupted job whose outcome was never recorded by looking at the
    /// target: applied, not applied, or uncertain. Nothing is redone; the user can roll back or plan again.
    /// </summary>
    public IReadOnlyList<RecoveredOperation> Recover(Guid jobId, IMachine target)
    {
        var results = new List<RecoveredOperation>();
        foreach (var record in journal.Read(jobId).Where(r => r.Outcome is null))
        {
            var intent = JournalIntent.FromJson(record.Intent);
            var classification = intent is null || record.OperationId.StartsWith(Journal.UndoPrefix, StringComparison.Ordinal)
                ? RecoveryClassification.Uncertain
                : Classify(intent, target);
            var status = classification switch
            {
                RecoveryClassification.Applied => Journal.RecoveredApplied,
                RecoveryClassification.NotApplied => Journal.RecoveredNotApplied,
                _ => Journal.Uncertain,
            };
            journal.RecordOutcome(jobId, record.OperationId, new JournalOutcome(status, "Settled by recovery after an interruption.").ToJson());
            results.Add(new RecoveredOperation(record.OperationId, intent?.Title ?? record.OperationId, classification));
        }

        journal.UpdateJob(jobId, JobStates.Interrupted);
        logger.LogWarning("Recovered interrupted restore job {JobId}: {Count} unfinished operations.", jobId, results.Count);
        return results;
    }

    private static RecoveryClassification Classify(JournalIntent intent, IMachine target)
    {
        switch (intent.Kind)
        {
            case "file":
            {
                var state = TargetProbe.FileState(target, intent.Target);
                return state == intent.After ? RecoveryClassification.Applied : state == intent.Before ? RecoveryClassification.NotApplied : RecoveryClassification.Uncertain;
            }

            case "directory":
                return target.FileSystem.DirectoryExists(intent.Target) ? RecoveryClassification.Applied : RecoveryClassification.NotApplied;

            case "repository":
                return !target.FileSystem.DirectoryExists(intent.Target) || TargetProbe.DirectoryIsEmpty(target, intent.Target)
                    ? RecoveryClassification.NotApplied
                    : RecoveryClassification.Uncertain;

            case "env":
            {
                var state = TargetProbe.VariableState(target, intent.Scope, intent.Target);
                return state == intent.After ? RecoveryClassification.Applied : state == intent.Before ? RecoveryClassification.NotApplied : RecoveryClassification.Uncertain;
            }

            case "path":
                return TargetProbe.HasPathEntry(TargetProbe.ReadVariable(target, intent.Scope, "Path")?.AsString(), intent.After!)
                    ? RecoveryClassification.Applied
                    : RecoveryClassification.NotApplied;

            default:
                return RecoveryClassification.Uncertain;
        }
    }
}
