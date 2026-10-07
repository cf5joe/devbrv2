namespace DevBR.Application.Restore;

public static class JobKinds
{
    public const string Restore = "restore";
}

public static class JobStates
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithProblems = "completed-with-problems";
    public const string Cancelled = "cancelled";

    /// <summary>Recovery found the job running with no live process: it was interrupted.</summary>
    public const string Interrupted = "interrupted";
    public const string RolledBack = "rolled-back";
    public const string PartiallyRolledBack = "partially-rolled-back";
}

public sealed record JournalJob(Guid JobId, string Kind, string State, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? Summary);

/// <summary>One journaled operation: the intent is written before the side effect, the outcome after it.</summary>
public sealed record JournalRecord(string OperationId, int Sequence, string Intent, string? Outcome, DateTimeOffset IntentAt, DateTimeOffset? OutcomeAt);

/// <summary>
/// Durable record of restore jobs. Each call commits before it returns, so after a crash the journal shows
/// every side effect that may have started (intent without outcome) and every one that finished.
/// </summary>
public interface IRestoreJournal
{
    void CreateJob(Guid jobId, string kind, string summary);

    void UpdateJob(Guid jobId, string state, string? summary = null);

    void RecordIntent(Guid jobId, string operationId, int sequence, string intent);

    void RecordOutcome(Guid jobId, string operationId, string outcome);

    JournalJob? GetJob(Guid jobId);

    IReadOnlyList<JournalJob> ListJobs(string kind, int limit);

    IReadOnlyList<JournalRecord> Read(Guid jobId);
}

/// <summary>Copies of content a restore replaced, kept private to the user until the job is discarded.</summary>
public interface IRollbackStore
{
    /// <returns>An identifier for the saved copy within the job.</returns>
    string Save(Guid jobId, Stream content);

    Stream Open(Guid jobId, string id);

    void DeleteJob(Guid jobId);
}

public enum PackageKind
{
    WinGet,
    EditorExtension,
}

/// <param name="Tool">For extensions, the editor command line (code, code-insiders, cursor).</param>
public sealed record PackageInstallRequest(PackageKind Kind, string PackageId, string? Version, string Tool);

public sealed record PackageInstallOutcome(bool Succeeded, int ExitCode, string Output);

/// <summary>Runs approved installations. Requests come only from validated recipes.</summary>
public interface IPackageInstaller
{
    Task<PackageInstallOutcome> InstallAsync(PackageInstallRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// What a restore job was approved to do, stored with the job. The elevated broker reads it from the
/// journal and accepts a machine change only when it matches one of these effects.
/// </summary>
public sealed record RestoreJobSummary(
    Guid PlanId,
    string ApprovalHash,
    Guid ArchiveId,
    string ArchivePath,
    string MachineName,
    IReadOnlyList<string> Effects,
    int Applied = 0,
    int Failed = 0,
    int Blocked = 0,
    int Skipped = 0,
    string? ReportHtmlPath = null,
    string? ReportJsonPath = null)
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web);

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Options);

    public static RestoreJobSummary? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<RestoreJobSummary>(json, Options);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The approved effects of a job that has not been fully rolled back (undo needs them too), provided they hash to the recorded approval;
    /// null for anything else (unknown, finished, or inconsistent jobs).
    /// </summary>
    public static IReadOnlySet<string>? ApprovedEffects(IRestoreJournal journal, Guid jobId)
    {
        var job = journal.GetJob(jobId);
        if (job is null || job.Kind != JobKinds.Restore || job.State == JobStates.RolledBack)
        {
            return null;
        }

        var summary = FromJson(job.Summary);
        if (summary is null || summary.Effects.Count == 0 || Machine.RestoreEffects.Hash(summary.Effects) != summary.ApprovalHash)
        {
            return null;
        }

        return summary.Effects.ToHashSet(StringComparer.Ordinal);
    }
}
