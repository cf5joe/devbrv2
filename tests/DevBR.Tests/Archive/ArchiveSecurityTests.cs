using System.Diagnostics;
using System.Text;
using DevBR.Application.Archive;
using DevBR.Archive;

namespace DevBR.Tests.Archive;

/// <summary>Hostile archives crafted byte by byte: nothing may be written outside, or before, validation succeeds.</summary>
public sealed class ArchiveSecurityTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly SevenZipArchiveService _service = new(Loggers.For<SevenZipArchiveService>());

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Raw(string name, params RawEntry[] entries)
    {
        var path = _temp.Combine(name);
        RawSevenZipWriter.Write(path, entries);
        return path;
    }

    private Task<ArchiveExtractResult> ExtractAllAsync(string archive, string output)
        => _service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, null, output, null), null, Ct);

    private static bool IsEmptyOrMissing(string folder)
        => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Any();

    [Fact]
    public async Task Hand_built_archives_are_readable_when_well_formed()
    {
        var archive = Raw("ok.devbr", new RawEntry("manifest.json", "{}"u8.ToArray()), new RawEntry("payload/a/b.txt", "hello"u8.ToArray()));

        var inspection = await _service.InspectAsync(new ArchiveInspectRequest(archive, null, ["manifest.json"], 1024), Ct);
        Assert.Equal(2, inspection.FileCount);
        Assert.Equal("{}", inspection.InlineEntries["manifest.json"]);

        var output = _temp.Combine("out");
        await ExtractAllAsync(archive, output);
        Assert.Equal("hello", await File.ReadAllTextAsync(Path.Combine(output, "payload", "a", "b.txt"), Ct));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("payload/../../escaped.txt")]
    [InlineData("payload/sub/../../../escaped.txt")]
    [InlineData("C:/Windows/Temp/devbr-escaped.txt")]
    [InlineData(@"C:\Windows\Temp\devbr-escaped.txt")]
    [InlineData("C:devbr-escaped.txt")]
    [InlineData("/escaped.txt")]
    [InlineData("//server/share/escaped.txt")]
    [InlineData("//?/C:/devbr-escaped.txt")]
    [InlineData("//./pipe/devbr")]
    [InlineData("payload/file.txt:hidden")]
    [InlineData("payload/file.txt::$DATA")]
    [InlineData("payload/CON")]
    [InlineData("payload/nul.txt")]
    [InlineData("payload/COM1.tar.gz")]
    [InlineData("payload/AUX .txt")]
    [InlineData("payload/trailing.")]
    [InlineData("payload/trailing ")]
    public async Task Unsafe_entry_names_are_rejected_before_anything_is_written(string name)
    {
        var archive = Raw("evil.devbr", new RawEntry("manifest.json", "{}"u8.ToArray()), new RawEntry(name, "pwned"u8.ToArray()));
        var output = _temp.Combine("out");

        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, (await Assert.ThrowsAsync<ArchiveException>(() =>
            _service.InspectAsync(new ArchiveInspectRequest(archive, null, [], 0), Ct))).Kind);
        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, (await Assert.ThrowsAsync<ArchiveException>(() => ExtractAllAsync(archive, output))).Kind);

        Assert.True(IsEmptyOrMissing(output));
        Assert.False(File.Exists(_temp.Combine("escaped.txt")));
        Assert.False(File.Exists(@"C:\Windows\Temp\devbr-escaped.txt"));
    }

    [Theory]
    [InlineData("payload/a.txt", "payload/a.txt")]
    [InlineData("payload/a.txt", "PAYLOAD/A.TXT")]
    [InlineData("payload/dir/a.txt", "payload/DIR/a.txt")]
    public async Task Duplicate_and_colliding_entries_are_rejected(string first, string second)
    {
        var archive = Raw("dup.devbr", new RawEntry(first, "a"u8.ToArray()), new RawEntry(second, "b"u8.ToArray()));
        var output = _temp.Combine("out");

        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, (await Assert.ThrowsAsync<ArchiveException>(() => ExtractAllAsync(archive, output))).Kind);
        Assert.True(IsEmptyOrMissing(output));
    }

    [Fact]
    public async Task Selecting_one_entry_twice_under_different_case_is_rejected()
    {
        var archive = Raw("sel.devbr", new RawEntry("payload/a.txt", "a"u8.ToArray()));
        var output = _temp.Combine("out");

        var error = await Assert.ThrowsAsync<ArchiveException>(() =>
            _service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, null, output, ["payload/a.txt", "PAYLOAD/A.txt"]), null, Ct));
        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, error.Kind);
        Assert.True(IsEmptyOrMissing(output));
    }

    [Theory]
    [InlineData(@"..\escaped.txt")]
    [InlineData(@"payload\..\..\escaped.txt")]
    [InlineData(@"\\server\share\escaped.txt")]
    [InlineData(@"\\.\pipe\devbr")]
    [InlineData(@"payload/..\..\escaped.txt")]
    public async Task Backslash_names_are_either_rejected_or_kept_inside_the_destination(string name)
    {
        // 7-Zip may store a raw backslash as a private-use character; either way nothing may land outside.
        var archive = Raw("bs.devbr", new RawEntry(name, "pwned"u8.ToArray()));
        var output = _temp.Combine("nested", "out");

        try
        {
            var result = await ExtractAllAsync(archive, output);
            Assert.All(result.Files, f => Assert.StartsWith(output + Path.DirectorySeparatorChar, f.DestinationPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArchiveException ex)
        {
            Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, ex.Kind);
        }

        Assert.False(File.Exists(_temp.Combine("nested", "escaped.txt")));
        Assert.False(File.Exists(_temp.Combine("escaped.txt")));
        Assert.Empty(Directory.GetFiles(_temp.Combine("nested")));
    }

    [Fact]
    public async Task Without_crcs_corruption_still_shows_in_the_reported_hash()
    {
        var content = new byte[4096];
        var archive = Raw("nocrc.devbr", new RawEntry("payload/data.bin", content) { WithCrc = false });
        var bytes = await File.ReadAllBytesAsync(archive, Ct);
        bytes[32 + 10] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes, Ct);

        var result = await ExtractAllAsync(archive, _temp.Combine("out"));

        // Callers compare this with the SHA-256 in entries.ndjson; it must describe the bytes actually written.
        Assert.NotEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), result.Files.Single().Sha256);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(result.Files.Single().DestinationPath))), result.Files.Single().Sha256);
    }

    [Theory]
    [InlineData(RawSevenZipWriter.ReparsePointAttribute)]
    [InlineData(RawSevenZipWriter.UnixSymlinkAttributes)]
    public async Task Link_entries_are_rejected_instead_of_being_followed_or_written(uint attributes)
    {
        var archive = Raw("link.devbr",
            new RawEntry("manifest.json", "{}"u8.ToArray()),
            new RawEntry("payload/link", Encoding.UTF8.GetBytes(@"..\..\..\Windows\System32")) { Attributes = attributes });
        var output = _temp.Combine("out");

        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, (await Assert.ThrowsAsync<ArchiveException>(() =>
            _service.InspectAsync(new ArchiveInspectRequest(archive, null, [], 0), Ct))).Kind);
        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, (await Assert.ThrowsAsync<ArchiveException>(() => ExtractAllAsync(archive, output))).Kind);
        Assert.True(IsEmptyOrMissing(output));
    }

    [Fact]
    public async Task A_junction_inside_the_destination_cannot_redirect_extraction()
    {
        var outside = _temp.Combine("outside");
        Directory.CreateDirectory(outside);
        var output = _temp.Combine("out");
        Directory.CreateDirectory(output);
        CreateJunction(Path.Combine(output, "payload"), outside);

        var archive = Raw("junction.devbr", new RawEntry("payload/planted.txt", "pwned"u8.ToArray()));

        Assert.Equal(ArchiveErrorKind.UnsafeEntryPath, (await Assert.ThrowsAsync<ArchiveException>(() => ExtractAllAsync(archive, output))).Kind);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task Declared_sizes_beyond_free_space_are_refused_before_writing()
    {
        // A "zip bomb" header: a few bytes that claim to unpack to a petabyte.
        var archive = Raw("bomb.devbr",
            new RawEntry("payload/small.txt", "ok"u8.ToArray()),
            new RawEntry("payload/bomb.bin", new byte[16]) { DeclaredSize = 1UL << 50, WithCrc = false });
        var output = _temp.Combine("out");

        var inspection = await _service.InspectAsync(new ArchiveInspectRequest(archive, null, [], 0), Ct);
        Assert.Equal((1L << 50) + 2, inspection.UncompressedBytes);

        Assert.Equal(ArchiveErrorKind.InsufficientSpace, (await Assert.ThrowsAsync<ArchiveException>(() => ExtractAllAsync(archive, output))).Kind);
        Assert.True(IsEmptyOrMissing(output));
    }

    [Fact]
    public async Task Inline_reads_respect_the_limit_whatever_the_entry_declares()
    {
        var archive = Raw("bigmanifest.devbr", new RawEntry("manifest.json", new byte[64 * 1024]));
        var error = await Assert.ThrowsAsync<ArchiveException>(() =>
            _service.InspectAsync(new ArchiveInspectRequest(archive, null, ["manifest.json"], 4096), Ct));
        Assert.Equal(ArchiveErrorKind.LimitExceeded, error.Kind);
    }

    [Fact]
    public async Task An_entry_shorter_than_its_declared_size_is_corrupt_and_leaves_no_file()
    {
        var archive = Raw("short.devbr", new RawEntry("payload/short.bin", new byte[100]) { DeclaredSize = 4096, WithCrc = false });
        var output = _temp.Combine("out");

        var error = await Assert.ThrowsAsync<ArchiveException>(() => ExtractAllAsync(archive, output));
        Assert.Contains(error.Kind, new[] { ArchiveErrorKind.Corrupt, ArchiveErrorKind.WrongPasswordOrCorrupt, ArchiveErrorKind.PasswordRequired });
        Assert.False(File.Exists(Path.Combine(output, "payload", "short.bin")));
    }

    [Fact]
    public async Task An_entry_never_produces_more_than_its_declared_size()
    {
        var archive = Raw("long.devbr", new RawEntry("payload/long.bin", new byte[64 * 1024]) { DeclaredSize = 16, WithCrc = false });
        var output = _temp.Combine("out");
        var target = Path.Combine(output, "payload", "long.bin");

        try
        {
            var result = await ExtractAllAsync(archive, output);
            Assert.Equal(16, result.Files.Single().Size);
        }
        catch (ArchiveException)
        {
            Assert.False(File.Exists(target));
            return;
        }

        Assert.Equal(16, new FileInfo(target).Length);
    }

    [Fact]
    public async Task Corrupt_payload_bytes_fail_the_checksum_and_leave_no_file()
    {
        var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("DEVBR-PAYLOAD-", 512)));
        var archive = Raw("crc.devbr", new RawEntry("payload/data.txt", content));

        var bytes = await File.ReadAllBytesAsync(archive, Ct);
        bytes[32 + 100] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes, Ct);

        var output = _temp.Combine("out");
        var error = await Assert.ThrowsAsync<ArchiveException>(() => ExtractAllAsync(archive, output));
        Assert.Contains(error.Kind, new[] { ArchiveErrorKind.Corrupt, ArchiveErrorKind.WrongPasswordOrCorrupt });
        Assert.False(File.Exists(Path.Combine(output, "payload", "data.txt")));
    }

    [Fact]
    public async Task A_damaged_header_is_reported_not_trusted()
    {
        var archive = Raw("hdr.devbr", new RawEntry("payload/a.txt", "a"u8.ToArray()));
        var bytes = await File.ReadAllBytesAsync(archive, Ct);
        bytes[^5] ^= 0xFF;
        await File.WriteAllBytesAsync(archive, bytes, Ct);

        var error = await Assert.ThrowsAsync<ArchiveException>(() => _service.InspectAsync(new ArchiveInspectRequest(archive, null, [], 0), Ct));
        Assert.Contains(error.Kind, new[] { ArchiveErrorKind.PasswordRequired, ArchiveErrorKind.Corrupt, ArchiveErrorKind.WrongPasswordOrCorrupt });
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(link), "mklink /J failed");
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}
