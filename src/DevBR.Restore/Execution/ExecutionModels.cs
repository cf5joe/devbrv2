using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBR.Application.Machine;
using DevBR.Application.Restore;
using DevBR.Domain;

namespace DevBR.Restore.Execution;

/// <param name="Request">The same request the approved preflight was made from; preflight runs again before anything is written.</param>
/// <param name="ReportFolder">Where the HTML and JSON reports are written.</param>
public sealed record RestoreExecutionRequest(
    RestoreRequest Request,
    PlanApproval Approval,
    IMachineWriter Writer,
    IElevationProvider Elevation,
    IPackageInstaller Installer,
    string ReportFolder);

public sealed record RestoreProgress(int Done, int Total, string Message);

public sealed record OperationReport(
    string OperationId,
    string ArtifactId,
    string ArtifactName,
    string Title,
    RestoreAction Action,
    RestoreStatus Status,
    VerificationLevel Verification,
    bool RollbackAvailable,
    string? Detail,
    IReadOnlyList<string> NextSteps);

public enum RestoreRunOutcome
{
    Completed,
    CompletedWithProblems,
    Cancelled,

    /// <summary>The computer changed so that the plan now has effects the user did not approve; nothing was written.</summary>
    ApprovalOutdated,
}

public sealed record RestoreRun(
    Guid JobId,
    RestoreRunOutcome Outcome,
    string Message,
    IReadOnlyList<OperationReport> Operations,
    RestorePreflight Preflight,
    string? ReportHtmlPath,
    string? ReportJsonPath,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public int Count(RestoreStatus status) => Operations.Count(o => o.Status == status);

    public bool CanRollBack => Operations.Any(o => o.RollbackAvailable);
}

/// <summary>What the journal records before a side effect. Never holds setting values or file content.</summary>
/// <param name="Kind">file, directory, repository, env, path or install.</param>
/// <param name="Before">The target's state before the change (absent, sha256:…, dir-absent, dir-empty, present).</param>
/// <param name="After">The state the change produces (sha256:… of the written content or value; the PATH entry for path).</param>
/// <param name="Copy">Rollback store copy: the previous file or value, or a repository's file manifest.</param>
internal sealed record JournalIntent(
    string Kind,
    string ArtifactId,
    string Title,
    string Target,
    string Scope,
    string? Before,
    string? After,
    string? Copy,
    bool Expandable,
    bool PreviousExpandable,
    bool Reversible)
{
    public string ToJson() => JsonSerializer.Serialize(this, Journal.Json);

    public static JournalIntent? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<JournalIntent>(json, Journal.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record JournalOutcome(string Status, string? Message)
{
    public string ToJson() => JsonSerializer.Serialize(this, Journal.Json);

    public static JournalOutcome? FromJson(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JournalOutcome>(json, Journal.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class Journal
{
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Undone = "undone";

    // Set by recovery for an operation whose intent was recorded but whose outcome was not.
    public const string RecoveredApplied = "recovered-applied";
    public const string RecoveredNotApplied = "recovered-not-applied";
    public const string Uncertain = "uncertain";

    public const string UndoPrefix = "undo:";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Sha(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    public static string Sha(string value) => "sha256:" + RestoreEffects.Sha256(value);

    public static string Sha(Stream content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}

/// <summary>A target that no longer matches what preflight saw. Nothing was written for it.</summary>
public sealed class TargetChangedException(string message) : Exception(message);

/// <summary>A repository's restored files, kept so rollback can prove the folder holds only what DevBR wrote.</summary>
internal sealed record RepositoryManifest(IReadOnlyList<RepositoryManifestEntry> Entries);

internal sealed record RepositoryManifestEntry(string RelativePath, bool Directory, string? Sha256);
