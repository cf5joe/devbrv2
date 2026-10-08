using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Archive;
using DevBR.Backup;
using DevBR.Discovery;
using DevBR.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using SharpSevenZip;

namespace DevBR.Tests.Backup;

/// <summary>Creates one genuine baseline backup and unpacks it, so each test can tamper with a copy.</summary>
public sealed class GenuineBackupFixture : IAsyncLifetime
{
    private readonly BackupFixture _machine = new();

    public TempDirectory Temp { get; } = new();

    public SevenZipArchiveService Archive { get; } = new(Loggers.For<SevenZipArchiveService>());

    public string Unpacked => Temp.Combine("unpacked");

    public async ValueTask InitializeAsync()
    {
        await _machine.InitializeAsync();
        var plan = new BackupPlanner().Plan(new BackupPlanRequest(_machine.Machine, _machine.Snapshot,
            [.. _machine.Snapshot.Artifacts.Where(a => a.SelectedByDefault)], [], DefaultExclusions.All, [Temp.Path]), null, CancellationToken.None);
        var result = await new BackupRunner(Archive, NullLogger<BackupRunner>.Instance).RunAsync(plan,
            new BackupRunOptions(Temp.Combine("genuine.devbr"), Temp.Combine("scratch"), CompressionPreset.Fast, null, false), null, CancellationToken.None);
        await Archive.ExtractSelectedAsync(new ArchiveExtractRequest(result.OutputPath!, null, Unpacked, null), null, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _machine.DisposeAsync();
        Temp.Dispose();
    }
}

/// <summary>Counts extraction calls so tests can prove a backup was rejected before anything was extracted.</summary>
internal sealed class SpyArchiveService(IArchiveService inner) : IArchiveService
{
    public int Extractions { get; private set; }

    public Task<ArchiveCreateResult> CreateAsync(ArchiveCreateRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
        => inner.CreateAsync(request, progress, cancellationToken);

    public Task<ArchiveInspection> InspectAsync(ArchiveInspectRequest request, CancellationToken cancellationToken)
        => inner.InspectAsync(request, cancellationToken);

    public Task<ArchiveExtractResult> ExtractSelectedAsync(ArchiveExtractRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        Extractions++;
        return inner.ExtractSelectedAsync(request, progress, cancellationToken);
    }
}

/// <summary>The Phase 6 "archive security" rows for backup indexes: every record is untrusted, bounded data.</summary>
public sealed class BackupReaderSecurityTests(GenuineBackupFixture fixture) : IClassFixture<GenuineBackupFixture>, IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Scratch => _temp.Combine("scratch");

    /// <summary>Copies the genuine backup, lets the test tamper with it, optionally re-signs the indexes, and repacks it.</summary>
    private string Tamper(Action<string> mutate, bool fixHashes = true)
    {
        var folder = _temp.Combine("work", Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(fixture.Unpacked, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(folder, Path.GetRelativePath(fixture.Unpacked, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        mutate(folder);

        if (fixHashes)
        {
            var manifestPath = Path.Combine(folder, "manifest.json");
            var manifest = BackupManifestReader.Parse(File.ReadAllText(manifestPath)).Manifest!;
            var hashes = manifest.IndexSha256.ToDictionary(
                p => p.Key,
                p => File.Exists(Path.Combine(folder, p.Key)) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(folder, p.Key)))) : p.Value);
            File.WriteAllText(manifestPath, BackupManifestReader.Serialize(manifest with { IndexSha256 = hashes }));
        }

        var archive = _temp.Combine($"{Guid.NewGuid():N}.devbr");
        new SharpSevenZipCompressor { ArchiveFormat = OutArchiveFormat.SevenZip, IncludeEmptyDirectories = true }.CompressDirectory(folder, archive);
        return archive;
    }

    private Task<BackupOverview> OpenAsync(string archive, IArchiveService? service = null)
        => new BackupReader(service ?? fixture.Archive).OpenAsync(archive, null, Scratch, Ct);

    private async Task<string> RejectedAsync(string archive)
    {
        var error = await Assert.ThrowsAsync<BackupFormatException>(() => OpenAsync(archive));
        Assert.True(!Directory.Exists(Scratch) || !Directory.EnumerateFileSystemEntries(Scratch).Any(), "the private catalog must be removed");
        return error.Message;
    }

    private static void EditManifest(string folder, Func<BackupManifest, BackupManifest> edit)
    {
        var path = Path.Combine(folder, "manifest.json");
        File.WriteAllText(path, BackupManifestReader.Serialize(edit(BackupManifestReader.Parse(File.ReadAllText(path)).Manifest!)));
    }

    private static void EditLines(string folder, string index, Func<List<string>, List<string>> edit)
    {
        var path = Path.Combine(folder, index);
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        File.WriteAllText(path, string.Join('\n', edit(lines)) + "\n");
    }

    private static string EditJson(string line, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(line)!.AsObject();
        edit(node);
        return node.ToJsonString();
    }

    [Fact]
    public async Task The_untampered_copy_opens()
    {
        var overview = await OpenAsync(Tamper(_ => { }));
        BackupReader.Close(overview);
        Assert.NotEmpty(overview.Artifacts);
    }

    [Fact]
    public async Task An_unsupported_major_version_is_rejected_before_anything_is_extracted()
    {
        var archive = Tamper(folder =>
        {
            var path = Path.Combine(folder, "manifest.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["formatVersion"]!["major"] = 2;
            json["unknownFutureField"] = "ignored";
            File.WriteAllText(path, json.ToJsonString());
        }, fixHashes: false);

        var spy = new SpyArchiveService(fixture.Archive);
        var error = await Assert.ThrowsAsync<BackupFormatException>(() => OpenAsync(archive, spy));

        Assert.Contains("2.x", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, spy.Extractions);
        Assert.False(Directory.Exists(Scratch) && Directory.EnumerateFileSystemEntries(Scratch).Any());
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"formatVersion\":\"1.0\"}")]
    [InlineData("{\"formatVersion\":{\"major\":1,\"minor\":0}")]
    [InlineData("{\"formatVersion\":{\"major\":1,\"minor\":0}}")]
    public async Task A_malformed_or_incomplete_manifest_is_rejected(string manifest)
    {
        var archive = Tamper(folder => File.WriteAllText(Path.Combine(folder, "manifest.json"), manifest), fixHashes: false);
        var spy = new SpyArchiveService(fixture.Archive);

        await Assert.ThrowsAsync<BackupFormatException>(() => OpenAsync(archive, spy));
        Assert.Equal(0, spy.Extractions);
    }

    [Fact]
    public async Task A_deeply_nested_manifest_is_rejected_without_recursion_trouble()
    {
        var nested = "{\"formatVersion\":{\"major\":1,\"minor\":0},\"x\":" + new string('[', 10_000) + new string(']', 10_000) + "}";
        var archive = Tamper(folder => File.WriteAllText(Path.Combine(folder, "manifest.json"), nested), fixHashes: false);
        await Assert.ThrowsAsync<BackupFormatException>(() => OpenAsync(archive));
    }

    [Fact]
    public async Task An_oversized_manifest_is_refused_without_being_read()
    {
        var archive = Tamper(folder => File.AppendAllText(Path.Combine(folder, "manifest.json"), new string(' ', (int)ArchiveContract.MaxManifestBytes + 1)), fixHashes: false);
        var error = await Assert.ThrowsAsync<ArchiveException>(() => OpenAsync(archive));
        Assert.Equal(ArchiveErrorKind.LimitExceeded, error.Kind);
    }

    [Theory]
    [InlineData("artifacts.ndjson")]
    [InlineData("entries.ndjson")]
    [InlineData("inventory.ndjson")]
    [InlineData("reports/backup-report.json")]
    public async Task Any_index_that_differs_from_its_manifest_hash_is_rejected(string index)
    {
        var archive = Tamper(folder => File.AppendAllText(Path.Combine(folder, index), " "), fixHashes: false);
        Assert.Contains("does not match the manifest", await RejectedAsync(archive), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_manifest_hash_is_rejected()
    {
        var archive = Tamper(folder => EditManifest(folder, m => m with
        {
            IndexSha256 = new Dictionary<string, string>(m.IndexSha256) { ["entries.ndjson"] = new string('0', 64) },
        }), fixHashes: false);
        Assert.Contains("does not match the manifest", await RejectedAsync(archive), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_required_index_missing_from_the_manifest_or_archive_is_rejected()
    {
        var unlisted = Tamper(folder => EditManifest(folder, m => m with
        {
            IndexSha256 = m.IndexSha256.Where(p => p.Key != "entries.ndjson").ToDictionary(p => p.Key, p => p.Value),
        }), fixHashes: false);
        Assert.Contains("required index", (await Assert.ThrowsAsync<BackupFormatException>(() => OpenAsync(unlisted))).Message, StringComparison.Ordinal);

        var absent = Tamper(folder => File.Delete(Path.Combine(folder, "entries.ndjson")), fixHashes: false);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(absent));
        Assert.True(error is BackupFormatException or ArchiveException, error.GetType().Name);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("not json")]
    [InlineData("array")]
    [InlineData("null")]
    [InlineData("unknown enum")]
    public async Task Malformed_or_truncated_index_records_are_rejected(string damage)
    {
        var archive = Tamper(folder => EditLines(folder, "entries.ndjson", lines =>
        {
            lines[^1] = damage switch
            {
                "truncated" => lines[^1][..(lines[^1].Length / 2)],
                "not json" => "this is not json",
                "array" => "[1,2,3]",
                "null" => "null",
                _ => lines[^1].Replace("\"File\"", "\"Device\"", StringComparison.Ordinal).Replace("\"Directory\"", "\"Device\"", StringComparison.Ordinal),
            };
            return lines;
        }));

        await RejectedAsync(archive);
    }

    [Fact]
    public async Task An_oversized_index_record_is_rejected()
    {
        var archive = Tamper(folder => EditLines(folder, "entries.ndjson", lines =>
        {
            lines.Add(new string('x', 5 * 1024 * 1024));
            return lines;
        }));
        Assert.Contains("oversized record", await RejectedAsync(archive), StringComparison.Ordinal);
    }

    [Fact]
    public void Index_lines_are_read_with_bounded_memory()
    {
        // An endless line: an unbounded reader would buffer it all; the bounded one stops at the limit.
        var source = new EndlessLineReader();
        Assert.Throws<InvalidDataException>(() => BoundedLineReader.ReadLines(source, 1024 * 1024).ToList());
        Assert.True(source.CharsRead < 1024 * 1024 + 16 * 1024, $"Read {source.CharsRead:N0} characters before failing");

        var lines = BoundedLineReader.ReadLines(new StringReader("a\r\nbb\n\nccc"), 3).ToList();
        Assert.Equal(["a", "bb", "", "ccc"], lines);
        Assert.Throws<InvalidDataException>(() => BoundedLineReader.ReadLines(new StringReader("abcd\n"), 3).ToList());
        Assert.Single(BoundedLineReader.ReadLines(new StringReader("abc\r\n"), 3));
    }

    [Theory]
    [InlineData("payload/../outside.txt", "unsafe path")]
    [InlineData("C:/Windows/evil.dll", "unsafe path")]
    [InlineData("payload/a0001/r0/CON.txt", "unsafe path")]
    [InlineData("payload/a0001/r0/file.txt:stream", "unsafe path")]
    [InlineData("manifest.json", "does not belong")]
    [InlineData("payload/a9999/r0/x.txt", "does not belong")]
    public async Task Entry_paths_must_be_safe_and_belong_to_a_listed_artifact(string archivePath, string expected)
    {
        var archive = Tamper(folder => EditLines(folder, "entries.ndjson", lines =>
        {
            lines[0] = EditJson(lines[0], o => o["archivePath"] = archivePath);
            return lines;
        }));
        Assert.Contains(expected, await RejectedAsync(archive), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_and_case_colliding_entries_are_rejected()
    {
        var archive = Tamper(folder => EditLines(folder, "entries.ndjson", lines =>
        {
            lines.Add(EditJson(lines[0], o =>
            {
                var parts = o["archivePath"]!.GetValue<string>().Split('/');
                o["archivePath"] = string.Join('/', parts.Take(2).Concat(parts.Skip(2).Select(p => p.ToUpperInvariant())));
            }));
            return lines;
        }));
        Assert.Contains("more than once", await RejectedAsync(archive), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_artifact_keys_are_rejected()
    {
        var archive = Tamper(folder => EditLines(folder, "artifacts.ndjson", lines =>
        {
            var key = JsonNode.Parse(lines[0])!["key"]!.GetValue<string>();
            lines[1] = EditJson(lines[1], o => o["key"] = key);
            return lines;
        }));
        Assert.Contains("duplicate key", await RejectedAsync(archive), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SymbolicLink", null)]
    [InlineData("Junction", null)]
    [InlineData("File", @"C:\Windows\System32")]
    [InlineData("Directory", @"..\..\..")]
    public async Task Link_entries_and_link_targets_are_rejected(string entryType, string? linkTarget)
    {
        var archive = Tamper(folder => EditLines(folder, "entries.ndjson", lines =>
        {
            lines[0] = EditJson(lines[0], o =>
            {
                o["entryType"] = entryType;
                o["linkTarget"] = linkTarget ?? @"C:\";
            });
            return lines;
        }));
        Assert.Contains("as a link", await RejectedAsync(archive), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_negative_size_is_rejected()
    {
        var archive = Tamper(folder => EditLines(folder, "entries.ndjson", lines =>
        {
            lines[0] = EditJson(lines[0], o => o["size"] = -1);
            return lines;
        }));
        Assert.Contains("invalid size", await RejectedAsync(archive), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_count_and_size_must_match_the_manifest()
    {
        var fewer = Tamper(folder => EditLines(folder, "entries.ndjson", lines => lines[..^1]));
        Assert.Contains("manifest declares", await RejectedAsync(fewer), StringComparison.Ordinal);

        var more = Tamper(folder => EditManifest(folder, m => m with { Totals = m.Totals with { EntryCount = m.Totals.EntryCount - 1 } }));
        Assert.Contains("manifest declares", await RejectedAsync(more), StringComparison.Ordinal);

        var larger = Tamper(folder => EditLines(folder, "entries.ndjson", lines =>
        {
            var index = lines.FindIndex(l => l.Contains("\"File\"", StringComparison.Ordinal));
            lines[index] = EditJson(lines[index], o => o["size"] = o["size"]!.GetValue<long>() + (1L << 40));
            return lines;
        }));
        Assert.Contains("manifest declares", await RejectedAsync(larger), StringComparison.Ordinal);

        var artifacts = Tamper(folder => EditManifest(folder, m => m with { Totals = m.Totals with { ArtifactCount = m.Totals.ArtifactCount + 1 } }));
        Assert.Contains("manifest declares", await RejectedAsync(artifacts), StringComparison.Ordinal);
    }

    private sealed class EndlessLineReader : TextReader
    {
        public long CharsRead { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            Array.Fill(buffer, 'x', index, count);
            CharsRead += count;
            return count;
        }
    }
}
