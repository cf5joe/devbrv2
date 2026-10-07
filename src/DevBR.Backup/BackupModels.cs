using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Application.Machine;
using DevBR.Discovery;
using DevBR.Domain;

namespace DevBR.Backup;

/// <summary>A regenerable-folder rule. Applied only to repositories and custom folders, never to tracked content.</summary>
public sealed record ExclusionRule(string FolderName, bool Enabled, string Description);

public static class DefaultExclusions
{
    public static IReadOnlyList<ExclusionRule> All { get; } =
    [
        new("node_modules", true, "npm/yarn/pnpm dependencies"),
        new("bin", true, ".NET and other build output"),
        new("obj", true, ".NET intermediate build output"),
        new(".venv", true, "Python virtual environment"),
        new("venv", true, "Python virtual environment"),
        new("__pycache__", true, "Python bytecode cache"),
        new(".pytest_cache", true, "pytest cache"),
        new(".mypy_cache", true, "mypy cache"),
        new(".tox", true, "tox environments"),
        new(".gradle", true, "Gradle cache"),
        new(".next", true, "Next.js build output"),
        new(".nuxt", true, "Nuxt build output"),
        new("target", false, "Rust/Maven build output (off by default: also a common source folder name)"),
        new("dist", false, "Bundled output (off by default: sometimes committed)"),
    ];
}

/// <param name="ProtectedPaths">Folders that must never be captured: the output file, scratch and DevBR's own data.</param>
public sealed record BackupPlanRequest(
    IMachine Machine,
    DiscoverySnapshot Snapshot,
    IReadOnlyList<MigrationArtifact> Selected,
    IReadOnlyList<string> CustomPaths,
    IReadOnlyList<ExclusionRule> Exclusions,
    IReadOnlyList<string> ProtectedPaths);

public enum CaptureKind
{
    Files,
    Environment,
    InventoryOnly,
}

public enum FindingLevel
{
    Information,
    Warning,
    Blocking,
}

/// <param name="Remediation">The exact next step, when the user can do something about it.</param>
public sealed record PlanFinding(FindingLevel Level, string? ArtifactId, string Message, string? Remediation = null);

public sealed record PlannedRoot(int Index, LogicalPath Logical, string SourcePath, bool IsDirectory);

/// <param name="RelativePath">Relative to the root, with backslashes. Empty for a file root.</param>
public sealed record PlannedFile(int RootIndex, string SourcePath, string RelativePath, long Size, DateTimeOffset LastWriteUtc, bool IsDirectory);

public sealed record AppliedExclusion(string Path, string Rule, long Files, long Bytes);

/// <param name="Key">Short archive folder name (a0001), so payload paths stay short and need no escaping.</param>
public sealed record PlannedArtifact(
    MigrationArtifact Artifact,
    string Key,
    CaptureKind Kind,
    IReadOnlyList<PlannedRoot> Roots,
    IReadOnlyList<PlannedFile> Files,
    IReadOnlyList<AppliedExclusion> Exclusions,
    bool Blocked)
{
    public long Bytes => Files.Sum(f => f.Size);

    public int FileCount => Files.Count(f => !f.IsDirectory);
}

public sealed record BackupPlan(
    Guid PlanId,
    IMachine Machine,
    DiscoverySnapshot Snapshot,
    IReadOnlyList<PlannedArtifact> Artifacts,
    IReadOnlyList<PlanFinding> Findings,
    IReadOnlyList<string> EncryptionReasons,
    IReadOnlyList<ExclusionRule> Exclusions)
{
    public bool RequiresEncryption => EncryptionReasons.Count > 0;

    public long Files => Artifacts.Where(a => !a.Blocked).Sum(a => (long)a.FileCount);

    public long Bytes => Artifacts.Where(a => !a.Blocked).Sum(a => a.Bytes);

    public bool HasBlockingFindings => Findings.Any(f => f.Level == FindingLevel.Blocking);

    /// <summary>A conservative upper bound for the archive: incompressible content plus per-entry overhead.</summary>
    public long EstimatedArchiveUpperBound => Bytes + (Files * 512) + (4L * 1024 * 1024);
}

public sealed record BackupRunOptions(
    string OutputPath,
    string ScratchRoot,
    CompressionPreset Compression,
    SecretText? Password,
    bool AllowOverwrite);

public enum BackupOutcome
{
    Succeeded,
    SucceededWithWarnings,
    Cancelled,
    EncryptionRequired,
    Failed,
}

/// <param name="Status">"Complete", "Incomplete" (some files could not be captured), "Inconsistent" (changed while captured) or "Blocked".</param>
public sealed record ArtifactCaptureResult(string ArtifactId, string DisplayName, string Status, long Files, long Bytes, IReadOnlyList<string> Warnings);

public sealed record BackupResult(
    BackupOutcome Outcome,
    string? OutputPath,
    long ArchiveBytes,
    long Files,
    long Bytes,
    bool Verified,
    bool Encrypted,
    IReadOnlyList<ArtifactCaptureResult> Artifacts,
    IReadOnlyList<string> Warnings,
    ArchiveErrorKind? ErrorKind,
    string Message,
    TimeSpan Duration);

/// <summary>Written to reports/backup-report.json inside the archive. Contains no secret values.</summary>
public sealed record BackupReport(
    Guid ArchiveId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool Encrypted,
    IReadOnlyList<ArtifactCaptureResult> Artifacts,
    IReadOnlyList<PlanFinding> Findings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<AppliedExclusion> Exclusions,
    string Disclosure);

/// <summary>One line of artifacts.ndjson.</summary>
public sealed record ArtifactIndexRecord(
    string Key,
    MigrationArtifact Artifact,
    CaptureKind Kind,
    IReadOnlyList<PlannedRoot> Roots,
    string Status,
    long Files,
    long Bytes,
    IReadOnlyList<string> Warnings);

/// <summary>One captured environment variable (environment artifacts' payload).</summary>
public sealed record CapturedVariable(string Name, string Scope, RegistryValueKind Kind, string Value);
