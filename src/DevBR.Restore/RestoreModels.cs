using System.Security.Cryptography;
using System.Text;
using DevBR.Application;
using DevBR.Application.Machine;
using DevBR.Application.Restore;
using DevBR.Backup;
using DevBR.Domain;

namespace DevBR.Restore;

/// <param name="SelectedKeys">Artifact keys (a0001…) chosen for restore.</param>
/// <param name="UserMappings">Root mappings chosen by the user; they override defaults by longest prefix.</param>
/// <param name="Decisions">Conflict decisions by operation id; anything not listed keeps the target's content.</param>
/// <param name="WorkFolder">Private folder for configuration files extracted for comparison.</param>
public sealed record RestoreRequest(
    BackupOverview Overview,
    SecretText? Password,
    IMachine Target,
    IReadOnlySet<string> SelectedKeys,
    IReadOnlyList<RootMapping> UserMappings,
    IReadOnlyDictionary<string, ConflictDecision> Decisions,
    string WorkFolder);

/// <summary>A restore operation with everything the preview shows about it.</summary>
/// <param name="Enabled">False for operations held back (e.g. a PATH entry whose folder does not exist yet).</param>
/// <param name="AllowedDecisions">The choices the user may make for this operation (empty when there is no conflict).</param>
public sealed record PlannedOperation(
    RestoreOperation Operation,
    string Title,
    string? Detail,
    string? ArchivePath,
    string? Sha256,
    long Size,
    PackageRecipe? Recipe,
    MergePreview? Merge,
    bool Enabled,
    IReadOnlyList<ConflictDecision> AllowedDecisions);

public sealed record ReinstallGuidance(string Name, string? SourceVersion, string Hint);

/// <summary>The outcome of preflight: findings, mappings, previews and the resulting plan. Nothing has been changed.</summary>
public sealed record RestorePreflight(
    RestorePlan Plan,
    IReadOnlyList<PlannedOperation> Operations,
    IReadOnlyList<PathMapping> Mappings,
    IReadOnlyList<PathRewrite> Rewrites,
    IReadOnlyList<McpServerInfo> McpServers,
    IReadOnlyList<ReinstallGuidance> Reinstall,
    IReadOnlySet<string> BlockedArtifacts)
{
    public IReadOnlyList<PreflightFinding> Findings => Plan.Findings;

    public bool HasBlocking => Findings.Any(f => f.Severity == FindingSeverity.Blocking);

    /// <summary>Operations that change something on the target (the effects the user approves).</summary>
    public IEnumerable<PlannedOperation> Effects => Operations.Where(o => o.Enabled && o.Operation.Action is not (RestoreAction.Skip or RestoreAction.ManualStep or RestoreAction.Validate));
}

/// <summary>
/// An approval of the effects a user reviewed. A later plan stays approved only if it introduces no new
/// effect; removing effects (for example after a recheck) does not require approving again.
/// </summary>
public sealed record PlanApproval(Guid PlanId, string ApprovalHash, IReadOnlySet<string> Effects, DateTimeOffset ApprovedAt)
{
    public static PlanApproval Approve(RestorePreflight preflight)
        => new(preflight.Plan.PlanId, preflight.Plan.ApprovalHash, EffectKeys(preflight), DateTimeOffset.UtcNow);

    public bool Covers(RestorePreflight current) => EffectKeys(current).IsSubsetOf(Effects);

    public static HashSet<string> EffectKeys(RestorePreflight preflight)
        => preflight.Effects.Select(EffectKey).ToHashSet(StringComparer.Ordinal);

    public static string EffectKey(PlannedOperation op)
        => string.Join('|', op.Operation.Action, op.Operation.Target.ToUpperInvariant(), op.Operation.ConflictDecision, op.Operation.Privilege,
            op.Recipe?.Preview ?? string.Empty, op.Sha256 ?? string.Empty, op.Operation.ExpectedTargetState ?? string.Empty);

    public static string Hash(IEnumerable<string> effects)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', effects.Order(StringComparer.Ordinal)))));
}
