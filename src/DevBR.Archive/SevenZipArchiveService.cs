using DevBR.Application;
using DevBR.Application.Archive;
using Microsoft.Extensions.Logging;
using SharpSevenZip;

namespace DevBR.Archive;

/// <summary>
/// 7z (LZMA2, non-solid) implementation. Runs only inside the unelevated archive worker. Extraction
/// never lets the native library choose file paths: every entry is validated and written through a
/// stream that DevBR controls.
/// </summary>
public sealed class SevenZipArchiveService : IArchiveService
{
    private static readonly byte[] SevenZipSignature = [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C];

    /// <summary>Free space kept in reserve beyond the bytes an extraction needs.</summary>
    private const long SpaceReserveBytes = 64L * 1024 * 1024;

    private static readonly Lock LibraryInitLock = new();
    private static bool _libraryInitialized;

    private readonly ILogger<SevenZipArchiveService> _logger;

    public SevenZipArchiveService(ILogger<SevenZipArchiveService> logger)
    {
        _logger = logger;
        EnsureLibrary();
    }

    public Task<ArchiveCreateResult> CreateAsync(ArchiveCreateRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Create(request, progress, cancellationToken), cancellationToken);

    public Task<ArchiveInspection> InspectAsync(ArchiveInspectRequest request, CancellationToken cancellationToken)
        => Task.Run(() => Inspect(request, cancellationToken), cancellationToken);

    public Task<ArchiveExtractResult> ExtractSelectedAsync(ArchiveExtractRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Extract(request, progress, cancellationToken), cancellationToken);

    private ArchiveCreateResult Create(ArchiveCreateRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.SourceDirectory));
        var output = Path.GetFullPath(request.OutputPath);
        var outputDirectory = Path.GetDirectoryName(output)
            ?? throw new ArchiveException(ArchiveErrorKind.InvalidRequest, "The output path has no parent folder.");

        if (!Directory.Exists(source))
        {
            throw new ArchiveException(ArchiveErrorKind.NotFound, "The folder to archive does not exist.");
        }

        if (!Directory.Exists(outputDirectory))
        {
            throw new ArchiveException(ArchiveErrorKind.NotFound, "The output folder does not exist.");
        }

        if (output.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveException(ArchiveErrorKind.InvalidRequest, "The backup file cannot be written inside the folder being archived.");
        }

        if (File.Exists(output) && !request.AllowOverwrite)
        {
            throw new ArchiveException(ArchiveErrorKind.OutputExists, "A file already exists at the output path and overwriting was not approved.");
        }

        var (fileCount, totalBytes) = MeasureTree(source, cancellationToken);
        var temp = $"{output}.partial-{Guid.NewGuid():N}";

        _logger.LogInformation("Creating archive: {FileCount} files, {Bytes} bytes, encrypted={Encrypted}", fileCount, totalBytes, request.Password is not null);

        try
        {
            var compressor = new SharpSevenZipCompressor
            {
                ArchiveFormat = OutArchiveFormat.SevenZip,
                CompressionMethod = request.Compression == CompressionPreset.Store ? CompressionMethod.Copy : CompressionMethod.Lzma2,
                CompressionLevel = request.Compression switch
                {
                    CompressionPreset.Store => CompressionLevel.None,
                    CompressionPreset.Fast => CompressionLevel.Fast,
                    CompressionPreset.Maximum => CompressionLevel.Ultra,
                    _ => CompressionLevel.Normal,
                },
                IncludeEmptyDirectories = true,
                PreserveDirectoryRoot = false,
                DirectoryStructure = true,
                EncryptHeaders = request.Password is not null,
            };

            // Non-solid so individual entries and indexes can be read without decompressing unrelated content.
            compressor.CustomParameters.Add("s", "off");
            compressor.CustomParameters.Add("mt", "on");

            compressor.FileCompressionStarted += (_, e) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    e.Cancel = true;
                }

                progress?.Report(new ArchiveProgress("Compressing", null, e.FileName, 0));
            };
            compressor.Compressing += (_, e) => progress?.Report(new ArchiveProgress("Compressing", e.PercentDone, null, 0));

            compressor.CompressDirectory(source, temp, request.Password?.Reveal() ?? string.Empty, "*", true);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ArchiveProgress("Verifying", null, null, 0));
            VerifyCreatedArchive(temp, request.Password, fileCount, cancellationToken);

            try
            {
                File.Move(temp, output, overwrite: request.AllowOverwrite);
            }
            catch (IOException ex) when (File.Exists(output))
            {
                throw new ArchiveException(ArchiveErrorKind.OutputExists, "A file appeared at the output path while the backup was being created.", ex);
            }

            return new ArchiveCreateResult(output, fileCount, totalBytes, new FileInfo(output).Length, request.Password is not null, Verified: true);
        }
        catch (Exception ex) when (ex is not ArchiveException and not OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Archive creation was cancelled.", ex, cancellationToken);
            }

            throw MapIoException(ex, "Creating the archive failed.");
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void VerifyCreatedArchive(string path, SecretText? password, long expectedFiles, CancellationToken cancellationToken)
    {
        using var extractor = Open(path, password);

        if (extractor.IsSolid)
        {
            throw new ArchiveException(ArchiveErrorKind.Internal, "The archive was written in solid mode; DevBR requires non-solid archives.");
        }

        var files = extractor.ArchiveFileData.LongCount(e => !e.IsDirectory);
        if (files != expectedFiles)
        {
            throw new ArchiveException(ArchiveErrorKind.Corrupt, $"Verification found {files} files but {expectedFiles} were archived.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!extractor.Check())
        {
            throw new ArchiveException(ArchiveErrorKind.Corrupt, "The new archive failed its integrity check.");
        }
    }

    private ArchiveInspection Inspect(ArchiveInspectRequest request, CancellationToken cancellationToken)
    {
        var path = RequireArchiveFile(request.ArchivePath);

        using var extractor = OpenForRead(path, request.Password);
        var entries = ReadEntries(extractor, cancellationToken);

        var inline = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wanted in request.InlineEntryPaths)
        {
            var normalized = ArchivePathValidator.Normalize(wanted);
            var entry = entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                continue;
            }

            if (entry.Size > request.MaxInlineBytes)
            {
                throw new ArchiveException(ArchiveErrorKind.LimitExceeded, $"'{normalized}' is larger than the {request.MaxInlineBytes:N0}-byte limit.");
            }

            using var buffer = new MemoryStream((int)entry.Size);
            using (var bounded = new BoundedHashingStream(buffer, request.MaxInlineBytes, cancellationToken))
            {
                ExtractEntry(extractor, entry.Index, bounded);
            }

            inline[normalized] = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        return new ArchiveInspection(
            path,
            new FileInfo(path).Length,
            Encrypted: entries.Any(e => e.Encrypted) || request.Password is not null,
            extractor.IsSolid,
            entries.LongCount(e => !e.IsDirectory),
            entries.Sum(e => e.Size),
            entries,
            inline);
    }

    private ArchiveExtractResult Extract(ArchiveExtractRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        var path = RequireArchiveFile(request.ArchivePath);

        using var extractor = OpenForRead(path, request.Password);
        var entries = ReadEntries(extractor, cancellationToken);

        IReadOnlyList<ArchiveEntryInfo> selected;
        if (request.EntryPaths is null)
        {
            selected = entries;
        }
        else
        {
            var byPath = entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
            selected = [.. request.EntryPaths.Select(p =>
                byPath.TryGetValue(ArchivePathValidator.Normalize(p), out var entry)
                    ? entry
                    : throw new ArchiveException(ArchiveErrorKind.InvalidRequest, $"The archive has no entry '{p}'."))];
        }

        // Validate every destination before writing anything.
        var destinationRoot = Path.GetFullPath(request.DestinationDirectory);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(ArchiveEntryInfo Entry, string Destination)>(selected.Count);
        foreach (var entry in selected)
        {
            var destination = ArchivePathValidator.ResolveUnder(destinationRoot, entry.Path);
            if (!seen.Add(destination))
            {
                throw new ArchiveException(ArchiveErrorKind.UnsafeEntryPath, $"The archive contains duplicate or case-colliding entries for '{entry.Path}'.");
            }

            plan.Add((entry, destination));
        }

        var requiredBytes = plan.Sum(p => p.Entry.Size);
        Directory.CreateDirectory(destinationRoot);
        var available = new DriveInfo(Path.GetPathRoot(destinationRoot)!).AvailableFreeSpace;
        if (requiredBytes + SpaceReserveBytes > available)
        {
            throw new ArchiveException(ArchiveErrorKind.InsufficientSpace, $"Extraction needs {requiredBytes:N0} bytes but only {available:N0} bytes are free.");
        }

        var results = new List<ExtractedFile>();
        long written = 0;
        foreach (var (entry, destination) in plan.OrderBy(p => p.Entry.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            progress?.Report(new ArchiveProgress("Extracting", requiredBytes == 0 ? null : (int)(written * 100 / requiredBytes), entry.Path, written));

            try
            {
                string hash;
                using (var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                using (var bounded = new BoundedHashingStream(file, entry.Size, cancellationToken))
                {
                    ExtractEntry(extractor, entry.Index, bounded);
                    if (bounded.BytesWritten != entry.Size)
                    {
                        throw new ArchiveException(ArchiveErrorKind.Corrupt, $"'{entry.Path}' produced {bounded.BytesWritten:N0} bytes; the archive declared {entry.Size:N0}.");
                    }

                    hash = bounded.GetHashHex();
                }

                written += entry.Size;
                results.Add(new ExtractedFile(entry.Path, destination, entry.Size, hash));
            }
            catch
            {
                TryDelete(destination);
                throw;
            }
        }

        progress?.Report(new ArchiveProgress("Extracting", 100, null, written));
        return new ArchiveExtractResult(results, written);
    }

    private static void ExtractEntry(SharpSevenZipExtractor extractor, int index, Stream destination)
    {
        try
        {
            extractor.ExtractFile(index, destination);
        }
        catch (Exception ex) when (FindInner<OperationCanceledException>(ex) is { } cancelled)
        {
            throw cancelled;
        }
        catch (Exception ex) when (FindInner<ArchiveException>(ex) is { } archiveError)
        {
            throw archiveError;
        }
        catch (Exception ex) when (ex is not ArchiveException and not OperationCanceledException)
        {
            throw new ArchiveException(ArchiveErrorKind.WrongPasswordOrCorrupt, "An archive entry could not be decompressed. The password may be wrong or the archive may be damaged.", ex);
        }
    }

    private static List<ArchiveEntryInfo> ReadEntries(SharpSevenZipExtractor extractor, CancellationToken cancellationToken)
    {
        var entries = new List<ArchiveEntryInfo>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var data in extractor.ArchiveFileData)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalized = ArchivePathValidator.Normalize(data.FileName);
            if (!seenPaths.Add(normalized))
            {
                throw new ArchiveException(ArchiveErrorKind.UnsafeEntryPath, $"The archive contains duplicate or case-colliding entries for '{normalized}'.");
            }

            entries.Add(new ArchiveEntryInfo(
                data.Index,
                normalized,
                data.IsDirectory,
                checked((long)data.Size),
                data.LastWriteTime == default ? null : new DateTimeOffset(DateTime.SpecifyKind(data.LastWriteTime, DateTimeKind.Local)).ToUniversalTime(),
                data.Crc,
                data.Encrypted));
        }

        return entries;
    }

    private static string RequireArchiveFile(string archivePath)
    {
        var path = Path.GetFullPath(archivePath);
        if (!File.Exists(path))
        {
            throw new ArchiveException(ArchiveErrorKind.NotFound, "The backup file does not exist.");
        }

        Span<byte> header = stackalloc byte[SevenZipSignature.Length];
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header.SequenceEqual(SevenZipSignature))
            {
                throw new ArchiveException(ArchiveErrorKind.NotADevbrArchive, "This file is not a DevBR backup.");
            }
        }

        return path;
    }

    /// <summary>Opens and forces the header to be read so password problems surface here, not mid-extraction.</summary>
    private static SharpSevenZipExtractor OpenForRead(string path, SecretText? password)
    {
        SharpSevenZipExtractor? extractor = null;
        try
        {
            extractor = Open(path, password);
            _ = extractor.FilesCount;
            _ = extractor.ArchiveFileData.Count;
            return extractor;
        }
        catch (Exception ex) when (ex is not ArchiveException)
        {
            extractor?.Dispose();
            throw password is null
                ? new ArchiveException(ArchiveErrorKind.PasswordRequired, "This backup could not be opened without a password. It may be encrypted or damaged.", ex)
                : new ArchiveException(ArchiveErrorKind.WrongPasswordOrCorrupt, "This backup could not be opened. The password may be wrong or the file may be damaged.", ex);
        }
    }

    private static SharpSevenZipExtractor Open(string path, SecretText? password)
        => password is null
            ? new SharpSevenZipExtractor(path, InArchiveFormat.SevenZip)
            : new SharpSevenZipExtractor(path, password.Reveal(), InArchiveFormat.SevenZip);

    private static (long Files, long Bytes) MeasureTree(string root, CancellationToken cancellationToken)
    {
        long files = 0, bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files++;
            bytes += file.Length;
        }

        return (files, bytes);
    }

    private static ArchiveException MapIoException(Exception ex, string message)
    {
        const int ErrorDiskFull = unchecked((int)0x80070070);
        const int ErrorHandleDiskFull = unchecked((int)0x80070027);

        var io = FindInner<IOException>(ex);
        if (io is not null && (io.HResult == ErrorDiskFull || io.HResult == ErrorHandleDiskFull))
        {
            return new ArchiveException(ArchiveErrorKind.InsufficientSpace, "The disk ran out of space.", ex);
        }

        return new ArchiveException(io is not null ? ArchiveErrorKind.IoFailure : ArchiveErrorKind.Internal, message, ex);
    }

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove temporary file {Path}", path);
        }
    }

    private static void EnsureLibrary()
    {
        lock (LibraryInitLock)
        {
            if (_libraryInitialized)
            {
                return;
            }

            var library = Path.Combine(AppContext.BaseDirectory, "x64", "7z.dll");
            if (!File.Exists(library))
            {
                throw new ArchiveException(ArchiveErrorKind.Internal, $"The bundled 7-Zip library is missing ({library}).");
            }

            SharpSevenZipBase.SetLibraryPath(library);
            _libraryInitialized = true;
        }
    }
}
