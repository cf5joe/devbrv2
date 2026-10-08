using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Domain;

namespace DevBR.Backup;

/// <summary>The backup is readable but not a valid DevBR archive (bad indexes, unsafe paths, mismatched hashes).</summary>
public sealed class BackupFormatException(string message) : Exception(message);

public sealed record ArtifactSummary(ArtifactIndexRecord Record, long EntryCount, long Bytes);

/// <summary>Everything shown when a backup is opened. Built from indexes only; no payload is extracted.</summary>
public sealed record BackupOverview(
    string ArchivePath,
    long ArchiveBytes,
    bool Encrypted,
    BackupManifest Manifest,
    IReadOnlyList<ArtifactSummary> Artifacts,
    BackupReport? Report,
    long InventoryCount,
    string CatalogFolder);

/// <summary>
/// Opens a .devbr file as untrusted input: the archive worker extracts only the index files into a
/// private catalog folder, each index is checked against the manifest's SHA-256, and every record is
/// parsed with size and count limits. Nothing in the archive is executed or interpreted as a command.
/// </summary>
public sealed partial class BackupReader(IArchiveService archive)
{
    private const int MaxLineBytes = 4 * 1024 * 1024;
    private const int MaxArtifacts = 100_000;

    private static readonly string[] Indexes =
    [
        ArchiveContract.ArtifactsIndexPath,
        ArchiveContract.EntriesIndexPath,
        ArchiveContract.InventoryIndexPath,
        ArchiveContract.CoveragePath,
        ArchiveContract.BackupReportPath,
    ];

    public async Task<BackupOverview> OpenAsync(string archivePath, SecretText? password, string scratchRoot, CancellationToken cancellationToken)
    {
        var inspection = await archive.InspectAsync(
            new ArchiveInspectRequest(archivePath, password, [ArchiveContract.ManifestPath], ArchiveContract.MaxManifestBytes, IncludeEntries: false),
            cancellationToken).ConfigureAwait(false);

        if (!inspection.InlineEntries.TryGetValue(ArchiveContract.ManifestPath, out var manifestJson))
        {
            throw new BackupFormatException("The file is a 7z archive but has no DevBR manifest.");
        }

        var parsed = BackupManifestReader.Parse(manifestJson);
        var manifest = parsed.Manifest ?? throw new BackupFormatException(parsed.Error!);

        foreach (var required in new[] { ArchiveContract.ArtifactsIndexPath, ArchiveContract.EntriesIndexPath })
        {
            if (!manifest.IndexSha256.ContainsKey(required))
            {
                throw new BackupFormatException($"The manifest does not list the required index {required}.");
            }
        }

        var catalog = Path.Combine(scratchRoot, $"inspect-{manifest.ArchiveId:N}-{Guid.NewGuid():N}");
        CreatePrivateDirectory(catalog);

        try
        {
            var wanted = Indexes.Where(manifest.IndexSha256.ContainsKey).ToList();
            var extracted = await archive.ExtractSelectedAsync(new ArchiveExtractRequest(archivePath, password, catalog, wanted), null, cancellationToken).ConfigureAwait(false);

            foreach (var index in wanted)
            {
                var file = extracted.Files.SingleOrDefault(f => string.Equals(f.ArchivePath, ArchivePathValidator.Normalize(index), StringComparison.OrdinalIgnoreCase))
                    ?? throw new BackupFormatException($"The index {index} is missing from the archive.");
                if (file.Size > ArchiveContract.MaxIndexBytes)
                {
                    throw new BackupFormatException($"The index {index} is larger than DevBR allows.");
                }

                if (!string.Equals(file.Sha256, manifest.IndexSha256[index], StringComparison.OrdinalIgnoreCase))
                {
                    throw new BackupFormatException($"The index {index} does not match the manifest. The backup may be damaged or altered.");
                }
            }

            var artifacts = ReadArtifacts(Path.Combine(catalog, "artifacts.ndjson"));
            if (artifacts.Count != manifest.Totals.ArtifactCount)
            {
                throw new BackupFormatException("The artifact index does not match the artifact count the manifest declares.");
            }

            var counts = CountEntries(Path.Combine(catalog, "entries.ndjson"), artifacts, manifest);
            var report = manifest.IndexSha256.ContainsKey(ArchiveContract.BackupReportPath)
                ? Deserialize<BackupReport>(File.ReadAllBytes(Path.Combine(catalog, "reports", "backup-report.json")))
                : null;
            var inventory = manifest.IndexSha256.ContainsKey(ArchiveContract.InventoryIndexPath)
                ? BoundedLines(Path.Combine(catalog, "inventory.ndjson")).LongCount()
                : 0;

            return new BackupOverview(
                inspection.ArchivePath, inspection.ArchiveBytes, inspection.Encrypted, manifest,
                [.. artifacts.Select(a => new ArtifactSummary(a, counts.GetValueOrDefault(a.Key).Count, counts.GetValueOrDefault(a.Key).Bytes))],
                report, inventory, catalog);
        }
        catch
        {
            Close(catalog);
            throw;
        }
    }

    /// <summary>Pages through the captured entries of one artifact, streaming the local index copy.</summary>
    public static IReadOnlyList<ArchiveEntry> ReadEntries(BackupOverview overview, string key, int skip, int take)
    {
        var prefix = $"payload/{key}/";
        return [.. BoundedLines(Path.Combine(overview.CatalogFolder, "entries.ndjson"))
            .Select(l => Deserialize<ArchiveEntry>(l))
            .Where(e => e.ArchivePath.StartsWith(prefix, StringComparison.Ordinal))
            .Skip(skip)
            .Take(take)];
    }

    public static void Close(BackupOverview overview) => Close(overview.CatalogFolder);

    private static void Close(string catalog)
    {
        try
        {
            if (Directory.Exists(catalog))
            {
                Directory.Delete(catalog, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<ArtifactIndexRecord> ReadArtifacts(string path)
    {
        var records = new List<ArtifactIndexRecord>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in BoundedLines(path))
        {
            var record = Deserialize<ArtifactIndexRecord>(line);
            if (!KeyPattern().IsMatch(record.Key) || !keys.Add(record.Key))
            {
                throw new BackupFormatException($"The artifact index contains an invalid or duplicate key '{record.Key}'.");
            }

            if (records.Count >= MaxArtifacts)
            {
                throw new BackupFormatException("The artifact index has more entries than DevBR allows.");
            }

            records.Add(record);
        }

        return records;
    }

    private static Dictionary<string, (long Count, long Bytes)> CountEntries(string path, List<ArtifactIndexRecord> artifacts, BackupManifest manifest)
    {
        var keys = artifacts.ToDictionary(a => a.Key, a => a.Artifact.Id, StringComparer.Ordinal);
        var counts = new Dictionary<string, (long Count, long Bytes)>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0, bytes = 0;

        foreach (var line in BoundedLines(path))
        {
            var entry = Deserialize<ArchiveEntry>(line);
            if (!ArchivePathValidator.TryNormalize(entry.ArchivePath, out var normalized, out var reason))
            {
                throw new BackupFormatException($"The entry index contains an unsafe path ({reason}).");
            }

            var parts = entry.ArchivePath.Split('/');
            if (parts.Length < 3 || parts[0] != "payload" || !keys.TryGetValue(parts[1], out var artifactId) || artifactId != entry.ArtifactId)
            {
                throw new BackupFormatException($"The entry '{normalized}' does not belong to a listed artifact.");
            }

            if (entry.EntryType is not (ArchiveEntryType.File or ArchiveEntryType.Directory) || entry.LinkTarget is not null)
            {
                throw new BackupFormatException($"The entry index describes '{normalized}' as a link, which DevBR backups never contain.");
            }

            if (!seen.Add(normalized) || entry.Size < 0)
            {
                throw new BackupFormatException($"The entry index lists '{normalized}' more than once or with an invalid size.");
            }

            if (++total > manifest.Totals.EntryCount)
            {
                throw new BackupFormatException("The entry index has more entries than the manifest declares.");
            }

            bytes = checked(bytes + entry.Size);
            var current = counts.GetValueOrDefault(parts[1]);
            counts[parts[1]] = entry.EntryType == ArchiveEntryType.File ? (current.Count + 1, current.Bytes + entry.Size) : current;
        }

        if (total != manifest.Totals.EntryCount || bytes != manifest.Totals.UncompressedBytes)
        {
            throw new BackupFormatException("The entry index does not match the entry count or size the manifest declares.");
        }

        return counts;
    }

    private static IEnumerable<string> BoundedLines(string path)
    {
        using var lines = BoundedLineReader.ReadLines(path, MaxLineBytes).GetEnumerator();
        while (true)
        {
            try
            {
                if (!lines.MoveNext())
                {
                    yield break;
                }
            }
            catch (InvalidDataException)
            {
                throw new BackupFormatException($"{Path.GetFileName(path)} contains an oversized record.");
            }

            if (lines.Current.Length > 0)
            {
                yield return lines.Current;
            }
        }
    }

    private static T Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, BackupRunner.IndexJson) ?? throw new BackupFormatException("An index record is empty.");
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException($"An index record is malformed ({ex.Message}).");
        }
    }

    private static T Deserialize<T>(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, BackupRunner.IndexJson) ?? throw new BackupFormatException("An index record is empty.");
        }
        catch (JsonException ex)
        {
            throw new BackupFormatException($"An index record is malformed ({ex.Message}).");
        }
    }

    private static void CreatePrivateDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.CreateDirectory(path);
    }

    [GeneratedRegex(@"^a\d{4,6}$")]
    private static partial Regex KeyPattern();
}
