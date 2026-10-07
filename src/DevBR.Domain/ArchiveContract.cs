namespace DevBR.Domain;

/// <summary>
/// The .devbr archive contract. It is versioned independently of the application: readers reject an
/// unsupported major version before extraction, and optional fields may evolve within a major version.
/// </summary>
public static class ArchiveContract
{
    public const int CurrentMajor = 1;
    public const int CurrentMinor = 0;
    public const string FileExtension = ".devbr";

    public const string ManifestPath = "manifest.json";
    public const string InventoryIndexPath = "inventory.ndjson";
    public const string ArtifactsIndexPath = "artifacts.ndjson";
    public const string EntriesIndexPath = "entries.ndjson";
    public const string CoveragePath = "coverage.json";
    public const string BackupReportPath = "reports/backup-report.json";
    public const string PayloadPrefix = "payload/";

    /// <summary>Upper bound for any index or manifest read into memory from an untrusted archive.</summary>
    public const long MaxIndexBytes = 256L * 1024 * 1024;
    public const long MaxManifestBytes = 4L * 1024 * 1024;
}

public sealed record ArchiveFormatVersion(int Major, int Minor)
{
    public bool IsSupported => Major == ArchiveContract.CurrentMajor;

    public override string ToString() => $"{Major}.{Minor}";
}

public sealed record BackupTotals(int ArtifactCount, long EntryCount, long UncompressedBytes);

public sealed record BackupManifest(
    ArchiveFormatVersion FormatVersion,
    string AppVersion,
    Guid ArchiveId,
    string SourceMachineName,
    string SourceOsDescription,
    string SourceArchitecture,
    DateTimeOffset CreatedAt,
    bool Encrypted,
    BackupTotals Totals,
    IReadOnlyDictionary<string, string> IndexSha256,
    IReadOnlyList<string> CaptureWarnings);

public enum ArchiveEntryType
{
    File,
    Directory,
    SymbolicLink,
    Junction,
}

public sealed record ArchiveEntry(
    string ArtifactId,
    string RelativePath,
    ArchiveEntryType EntryType,
    long Size,
    DateTimeOffset LastWriteTimeUtc,
    string? Sha256,
    FileAttributes Attributes,
    string? LinkTarget);
