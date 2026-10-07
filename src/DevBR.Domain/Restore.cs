namespace DevBR.Domain;

/// <summary>Warnings never block; blocking findings stop only the artifacts that depend on them.</summary>
public enum FindingSeverity
{
    Information,
    Warning,
    Blocking,
}

public sealed record PreflightFinding(
    string Id,
    FindingSeverity Severity,
    IReadOnlyList<string> AffectedArtifactIds,
    string? Prerequisite,
    string Problem,
    string WhyItMatters,
    IReadOnlyList<string> NextSteps,
    bool CanRecheck);

public enum RestoreAction
{
    CreateFile,
    ReplaceFile,
    MergeStructuredSettings,
    RestoreAlongside,
    SetEnvironmentVariable,
    AppendPathEntry,
    InstallDependency,
    RestoreRepository,
    Skip,
}

public enum ConflictDecision
{
    NoConflict,
    KeepExisting,
    UseBackup,
    Merge,
    RestoreAlongside,
}

public enum PrivilegeRequirement
{
    User,
    Elevated,
}

/// <summary>Installers can have effects DevBR cannot reverse; those are kept separate from rollback-capable changes.</summary>
public enum Reversibility
{
    RollbackCapable,
    Irreversible,
}

public sealed record RestoreOperation(
    string Id,
    string ArtifactId,
    IReadOnlyList<string> DependsOn,
    string Target,
    RestoreAction Action,
    ConflictDecision ConflictDecision,
    PrivilegeRequirement Privilege,
    Reversibility Reversibility,
    string? ExpectedTargetState);

public enum RestoreStatus
{
    Applied,
    Skipped,
    Failed,
    Blocked,
}

/// <summary>Keeps "restored" separate from "functionally verified".</summary>
public enum VerificationLevel
{
    NotVerified,
    ConfigurationApplied,
    FunctionallyVerified,
    VerificationFailed,
}

public sealed record RestoreResult(
    string OperationId,
    string ArtifactId,
    RestoreStatus Status,
    VerificationLevel Verification,
    bool RollbackAvailable,
    IReadOnlyList<string> NextSteps);
