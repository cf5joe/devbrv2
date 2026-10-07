namespace DevBR.Application.Archive;

public enum CompressionPreset
{
    Store,
    Fast,
    Normal,
    Maximum,
}

/// <param name="SourceDirectory">The staging root; its contents become the archive root.</param>
/// <param name="Password">Non-null enables AES-256 with header encryption (file names hidden).</param>
/// <param name="AllowOverwrite">An existing output is never replaced without an explicit overwrite decision.</param>
public sealed record ArchiveCreateRequest(
    string SourceDirectory,
    string OutputPath,
    SecretText? Password,
    CompressionPreset Compression,
    bool AllowOverwrite);

public sealed record ArchiveCreateResult(
    string OutputPath,
    long FileCount,
    long UncompressedBytes,
    long ArchiveBytes,
    bool Encrypted,
    bool Verified);

/// <param name="InlineEntryPaths">Small entries (e.g. manifest.json) to return in the result, bounded by <paramref name="MaxInlineBytes"/>.</param>
public sealed record ArchiveInspectRequest(
    string ArchivePath,
    SecretText? Password,
    IReadOnlyList<string> InlineEntryPaths,
    long MaxInlineBytes);

public sealed record ArchiveEntryInfo(
    int Index,
    string Path,
    bool IsDirectory,
    long Size,
    DateTimeOffset? LastWriteTimeUtc,
    uint Crc,
    bool Encrypted);

public sealed record ArchiveInspection(
    string ArchivePath,
    long ArchiveBytes,
    bool Encrypted,
    bool IsSolid,
    long FileCount,
    long UncompressedBytes,
    IReadOnlyList<ArchiveEntryInfo> Entries,
    IReadOnlyDictionary<string, string> InlineEntries);

/// <param name="EntryPaths">Entries to extract; null extracts everything.</param>
public sealed record ArchiveExtractRequest(
    string ArchivePath,
    SecretText? Password,
    string DestinationDirectory,
    IReadOnlyList<string>? EntryPaths);

public sealed record ExtractedFile(string ArchivePath, string DestinationPath, long Size, string Sha256);

public sealed record ArchiveExtractResult(IReadOnlyList<ExtractedFile> Files, long Bytes);

public sealed record ArchiveProgress(string Stage, int? Percent, string? CurrentItem, long BytesProcessed);

public enum ArchiveErrorKind
{
    InvalidRequest,
    NotFound,
    NotADevbrArchive,
    PasswordRequired,
    WrongPasswordOrCorrupt,
    Corrupt,
    OutputExists,
    UnsafeEntryPath,
    InsufficientSpace,
    LimitExceeded,
    IoFailure,
    WorkerUnavailable,
    Internal,
}

/// <summary>A failure the UI can explain specifically, rather than a generic exception message.</summary>
public sealed class ArchiveException(ArchiveErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ArchiveErrorKind Kind { get; } = kind;
}

/// <summary>
/// Compression, inspection, extraction and verification. The GUI uses a proxy that runs these in the
/// unelevated archive worker so native-library failures cannot terminate the GUI.
/// </summary>
public interface IArchiveService
{
    Task<ArchiveCreateResult> CreateAsync(ArchiveCreateRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken);

    Task<ArchiveInspection> InspectAsync(ArchiveInspectRequest request, CancellationToken cancellationToken);

    Task<ArchiveExtractResult> ExtractSelectedAsync(ArchiveExtractRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken);
}
