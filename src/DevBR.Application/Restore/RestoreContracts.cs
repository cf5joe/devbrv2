using DevBR.Domain;

namespace DevBR.Application.Restore;

/// <param name="ApprovalHash">Hash of the plan the user approved. Any change that adds effects invalidates it.</param>
public sealed record RestorePlan(
    Guid PlanId,
    Guid ArchiveId,
    IReadOnlyList<RestoreOperation> Operations,
    IReadOnlyList<PreflightFinding> Findings,
    string ApprovalHash);

public sealed record RestoreJob(Guid JobId, Guid PlanId, DateTimeOffset StartedAt);

public sealed record RestoreExecutionResult(
    RestoreJob Job,
    IReadOnlyList<RestoreResult> Results,
    bool Cancelled);

public sealed record RollbackResult(
    RestoreJob Job,
    IReadOnlyList<string> RolledBack,
    IReadOnlyList<string> Failed);

/// <summary>Performs writes, privilege checks, journaling and rollback for every adapter consistently.</summary>
public interface IRestoreExecutor
{
    Task<RestoreExecutionResult> ExecuteAsync(RestorePlan plan, IProgress<OperationEvent>? progress, CancellationToken cancellationToken);
}

public interface IRollbackService
{
    Task<RollbackResult> RollbackAsync(RestoreJob job, CancellationToken cancellationToken);
}
