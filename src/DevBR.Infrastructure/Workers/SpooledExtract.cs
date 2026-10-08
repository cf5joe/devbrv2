using System.Text;
using System.Text.Json;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Ipc;

namespace DevBR.Infrastructure.Workers;

/// <summary>
/// An extraction whose entry list and results travel through files instead of IPC frames. A restore of a
/// large folder names hundreds of thousands of entries, far beyond one 1 MB frame, so the GUI writes the
/// list to a private spool folder and the worker writes one result per line next to it.
/// </summary>
/// <param name="EntryListPath">UTF-8, one archive path per line; null extracts everything.</param>
/// <param name="ResultPath">Where the worker writes one <see cref="ExtractedFile"/> JSON object per line.</param>
public sealed record SpooledExtractRequest(string ArchivePath, SecretText? Password, string DestinationDirectory, string? EntryListPath, string ResultPath);

public sealed record SpooledExtractResult(long Files, long Bytes);

public static class SpooledExtract
{
    public const string Operation = "archive.extractSpooled";

    /// <summary>Requests naming more entries than this are spooled; smaller ones fit comfortably in one frame.</summary>
    public const int InlineEntryLimit = 256;

    public static bool ShouldSpool(ArchiveExtractRequest request) => request.EntryPaths is null || request.EntryPaths.Count > InlineEntryLimit;

    /// <summary>Worker side: runs the extraction and writes its results to <see cref="SpooledExtractRequest.ResultPath"/>.</summary>
    public static async Task<SpooledExtractResult> RunAsync(IArchiveService archive, SpooledExtractRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        List<string>? paths = null;
        if (request.EntryListPath is not null)
        {
            paths = [.. File.ReadLines(request.EntryListPath, Encoding.UTF8).Where(l => l.Length > 0)];
        }

        var result = await archive.ExtractSelectedAsync(new ArchiveExtractRequest(request.ArchivePath, request.Password, request.DestinationDirectory, paths), progress, cancellationToken)
            .ConfigureAwait(false);

        using (var writer = new StreamWriter(new FileStream(request.ResultPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), new UTF8Encoding(false)))
        {
            foreach (var file in result.Files)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(file, IpcJson.Options)).ConfigureAwait(false);
            }
        }

        return new SpooledExtractResult(result.Files.Count, result.Bytes);
    }

    public static void WriteEntryList(string path, IEnumerable<string> entries)
    {
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), new UTF8Encoding(false));
        foreach (var entry in entries)
        {
            if (entry.Contains('\n', StringComparison.Ordinal) || entry.Contains('\r', StringComparison.Ordinal))
            {
                throw new ArchiveException(ArchiveErrorKind.InvalidRequest, "An entry path contains a line break.");
            }

            writer.WriteLine(entry);
        }
    }

    public static List<ExtractedFile> ReadResults(string path, long expected)
    {
        var files = new List<ExtractedFile>((int)Math.Min(expected, 1_000_000));
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (line.Length > 0)
            {
                files.Add(JsonSerializer.Deserialize<ExtractedFile>(line, IpcJson.Options)
                    ?? throw new ArchiveException(ArchiveErrorKind.Internal, "The archive worker returned an empty result."));
            }
        }

        if (files.Count != expected)
        {
            throw new ArchiveException(ArchiveErrorKind.Internal, "The archive worker's result list is incomplete.");
        }

        return files;
    }
}
