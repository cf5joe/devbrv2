using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Archive;
using SharpSevenZip;

namespace DevBR.Tests.Archive;

public sealed class SevenZipArchiveServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SevenZipArchiveService _service = new(Loggers.For<SevenZipArchiveService>());

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CreateSource(out Dictionary<string, string> hashes)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["manifest.json"] = "{\"formatVersion\":{\"major\":1,\"minor\":0}}"u8.ToArray(),
            [@"payload\unicode\Привет мир — 世界 ✓.txt"] = Encoding.UTF8.GetBytes("unicode ✓"),
            [@"payload\spaces in name\notes.md"] = "# notes"u8.ToArray(),
            [@"payload\empty.txt"] = [],
            [@"payload\random.bin"] = RandomNumberGenerator.GetBytes(256 * 1024),
        };

        hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, content) in files)
        {
            _temp.WriteFile(Path.Combine("source", relative), content);
            hashes[relative] = Convert.ToHexStringLower(SHA256.HashData(content));
        }

        Directory.CreateDirectory(_temp.Combine("source", "payload", "empty folder"));
        return _temp.Combine("source");
    }

    [Fact]
    public async Task Unencrypted_round_trip_preserves_bytes_names_and_empty_folders()
    {
        var source = CreateSource(out var hashes);
        var archive = _temp.Combine("backup.devbr");

        var created = await _service.CreateAsync(new ArchiveCreateRequest(source, archive, null, CompressionPreset.Normal, false), null, Ct);
        Assert.True(created.Verified);
        Assert.Equal(hashes.Count, created.FileCount);
        Assert.False(created.Encrypted);
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.partial-*"));

        var inspection = await _service.InspectAsync(new ArchiveInspectRequest(archive, null, ["manifest.json"], 4096), Ct);
        Assert.False(inspection.IsSolid);
        Assert.False(inspection.Encrypted);
        Assert.Contains(inspection.Entries, e => e.Path == @"payload\unicode\Привет мир — 世界 ✓.txt");
        Assert.Contains("formatVersion", inspection.InlineEntries["manifest.json"], StringComparison.Ordinal);

        var output = _temp.Combine("out");
        var extracted = await _service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, null, output, null), null, Ct);
        foreach (var (relative, hash) in hashes)
        {
            Assert.Equal(hash, extracted.Files.Single(f => f.ArchivePath == relative).Sha256);
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(output, relative)))));
        }

        Assert.True(Directory.Exists(Path.Combine(output, "payload", "empty folder")));
    }

    [Fact]
    public async Task Selected_entries_are_extracted_without_the_rest()
    {
        var source = CreateSource(out _);
        var archive = _temp.Combine("backup.devbr");
        await _service.CreateAsync(new ArchiveCreateRequest(source, archive, null, CompressionPreset.Fast, false), null, Ct);

        var output = _temp.Combine("out");
        var result = await _service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, null, output, ["payload/spaces in name/notes.md"]), null, Ct);

        Assert.Single(result.Files);
        Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Encrypted_archive_hides_names_and_rejects_wrong_password()
    {
        var source = CreateSource(out var hashes);
        var archive = _temp.Combine("secret.devbr");
        var password = new SecretText("correct horse battery staple");

        var created = await _service.CreateAsync(new ArchiveCreateRequest(source, archive, password, CompressionPreset.Fast, false), null, Ct);
        Assert.True(created.Encrypted);

        // File names must not appear in the raw bytes when headers are encrypted.
        var raw = Encoding.Unicode.GetString(await File.ReadAllBytesAsync(archive, Ct));
        Assert.DoesNotContain("manifest.json", raw, StringComparison.Ordinal);

        var noPassword = await Assert.ThrowsAsync<ArchiveException>(() => _service.InspectAsync(new ArchiveInspectRequest(archive, null, [], 0), Ct));
        Assert.Equal(ArchiveErrorKind.PasswordRequired, noPassword.Kind);

        var wrong = await Assert.ThrowsAsync<ArchiveException>(() => _service.InspectAsync(new ArchiveInspectRequest(archive, new SecretText("wrong"), [], 0), Ct));
        Assert.Equal(ArchiveErrorKind.WrongPasswordOrCorrupt, wrong.Kind);

        var inspection = await _service.InspectAsync(new ArchiveInspectRequest(archive, password, [], 0), Ct);
        Assert.True(inspection.Encrypted);
        Assert.False(inspection.IsSolid);

        var output = _temp.Combine("out");
        var extracted = await _service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, password, output, null), null, Ct);
        Assert.All(hashes, pair => Assert.Equal(pair.Value, extracted.Files.Single(f => f.ArchivePath == pair.Key).Sha256));
    }

    [Fact]
    public async Task Existing_output_is_never_replaced_without_approval()
    {
        var source = CreateSource(out _);
        var archive = _temp.WriteFile("existing.devbr", "keep me"u8.ToArray());

        var error = await Assert.ThrowsAsync<ArchiveException>(() =>
            _service.CreateAsync(new ArchiveCreateRequest(source, archive, null, CompressionPreset.Fast, AllowOverwrite: false), null, Ct));

        Assert.Equal(ArchiveErrorKind.OutputExists, error.Kind);
        Assert.Equal("keep me", await File.ReadAllTextAsync(archive, Ct));

        var replaced = await _service.CreateAsync(new ArchiveCreateRequest(source, archive, null, CompressionPreset.Fast, AllowOverwrite: true), null, Ct);
        Assert.True(replaced.Verified);
    }

    [Fact]
    public async Task Output_inside_the_source_folder_is_refused()
    {
        var source = CreateSource(out _);
        var error = await Assert.ThrowsAsync<ArchiveException>(() =>
            _service.CreateAsync(new ArchiveCreateRequest(source, Path.Combine(source, "self.devbr"), null, CompressionPreset.Fast, false), null, Ct));
        Assert.Equal(ArchiveErrorKind.InvalidRequest, error.Kind);
    }

    [Fact]
    public async Task Cancelled_creation_leaves_no_output_or_temporary_file()
    {
        var source = _temp.Combine("big");
        for (var i = 0; i < 40; i++)
        {
            _temp.WriteFile(Path.Combine("big", $"file{i}.bin"), RandomNumberGenerator.GetBytes(1024 * 1024));
        }

        var archive = _temp.Combine("cancelled.devbr");
        using var cts = new CancellationTokenSource();
        var progress = new SynchronousProgress<ArchiveProgress>(p =>
        {
            if (p.CurrentItem is not null)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.CreateAsync(new ArchiveCreateRequest(source, archive, null, CompressionPreset.Normal, false), progress, cts.Token));

        Assert.False(File.Exists(archive));
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.partial-*"));
    }

    [Fact]
    public async Task Non_7z_files_are_rejected_before_parsing()
    {
        var fake = _temp.WriteFile("fake.devbr", "PK\u0003\u0004 definitely a zip"u8.ToArray());
        var error = await Assert.ThrowsAsync<ArchiveException>(() => _service.InspectAsync(new ArchiveInspectRequest(fake, null, [], 0), Ct));
        Assert.Equal(ArchiveErrorKind.NotADevbrArchive, error.Kind);
    }

    [Fact]
    public async Task Truncated_archive_is_reported_as_unreadable()
    {
        var source = CreateSource(out _);
        var archive = _temp.Combine("backup.devbr");
        await _service.CreateAsync(new ArchiveCreateRequest(source, archive, null, CompressionPreset.Fast, false), null, Ct);

        var bytes = await File.ReadAllBytesAsync(archive, Ct);
        var truncated = _temp.WriteFile("truncated.devbr", bytes[..(bytes.Length / 2)]);

        var error = await Assert.ThrowsAsync<ArchiveException>(() => _service.InspectAsync(new ArchiveInspectRequest(truncated, null, [], 0), Ct));
        Assert.Contains(error.Kind, new[] { ArchiveErrorKind.PasswordRequired, ArchiveErrorKind.Corrupt, ArchiveErrorKind.WrongPasswordOrCorrupt });
    }

    [Fact]
    public async Task Archive_with_traversal_entry_is_rejected_before_any_write()
    {
        var archive = _temp.Combine("evil.devbr");
        WriteRawArchive(archive, new Dictionary<string, byte[]>
        {
            ["manifest.json"] = "{}"u8.ToArray(),
            [@"..\escaped.txt"] = "pwned"u8.ToArray(),
        });

        var output = _temp.Combine("out");
        var error = await Assert.ThrowsAsync<ArchiveException>(() =>
            _service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, null, output, null), null, Ct));

        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, error.Kind);
        Assert.False(File.Exists(_temp.Combine("escaped.txt")));
        Assert.False(Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any());
    }

    [Fact]
    public async Task Archive_with_case_colliding_entries_is_rejected()
    {
        var archive = _temp.Combine("collide.devbr");
        WriteRawArchive(archive, new Dictionary<string, byte[]>
        {
            ["payload/Settings.json"] = "a"u8.ToArray(),
            ["payload/settings.json"] = "b"u8.ToArray(),
        });

        var error = await Assert.ThrowsAsync<ArchiveException>(() => _service.InspectAsync(new ArchiveInspectRequest(archive, null, [], 0), Ct));
        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, error.Kind);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public async Task Large_file_round_trip_streams_with_bounded_memory()
    {
        const int sizeMb = 256;
        var path = _temp.Combine("large", "big.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string expected;
        await using (var file = File.Create(path))
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < sizeMb; i++)
            {
                RandomNumberGenerator.Fill(chunk);
                sha.AppendData(chunk);
                await file.WriteAsync(chunk, Ct);
            }

            expected = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        using var process = Process.GetCurrentProcess();
        GC.Collect();
        process.Refresh();
        var baseline = process.PrivateMemorySize64;
        long peak = baseline;
        var sampler = new SynchronousProgress<ArchiveProgress>(_ =>
        {
            process.Refresh();
            peak = Math.Max(peak, process.PrivateMemorySize64);
        });

        var archive = _temp.Combine("large.devbr");
        await _service.CreateAsync(new ArchiveCreateRequest(_temp.Combine("large"), archive, null, CompressionPreset.Fast, false), sampler, Ct);
        var result = await _service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, null, _temp.Combine("out"), null), sampler, Ct);

        Assert.Equal(expected, result.Files.Single().Sha256);
        // A 256 MB file must not be buffered in memory. LZMA2 dictionaries and threads need some room.
        Assert.True(peak - baseline < 200L * 1024 * 1024, $"Memory grew by {(peak - baseline) / (1024 * 1024)} MB");
    }

    /// <summary>Writes entries with arbitrary names, bypassing DevBR's own path rules, to simulate hostile archives.</summary>
    private static void WriteRawArchive(string path, Dictionary<string, byte[]> entries)
    {
        var streams = entries.ToDictionary(e => e.Key, e => new StreamWithAttributes(new MemoryStream(e.Value), null, null, null));
        try
        {
            var compressor = new SharpSevenZipCompressor { ArchiveFormat = OutArchiveFormat.SevenZip };
            compressor.CompressStreamDictionary(streams, path);
        }
        finally
        {
            foreach (var entry in streams.Values)
            {
                entry.Stream.Dispose();
            }
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
