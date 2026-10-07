using System.Text.Json;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Domain;
using DevBR.Ipc;

namespace DevBR.Tests.Application;

public sealed class ContractTests
{
    private static BackupManifest SampleManifest(int major = ArchiveContract.CurrentMajor) => new(
        new ArchiveFormatVersion(major, 0),
        "0.1.0",
        Guid.NewGuid(),
        "WORKSTATION",
        "Windows 11 Pro 10.0.26200",
        "X64",
        DateTimeOffset.UtcNow,
        Encrypted: false,
        new BackupTotals(3, 42, 1234),
        new Dictionary<string, string> { ["entries.ndjson"] = "abc" },
        ["Repository C:\\src\\app had uncommitted changes while it was captured."]);

    [Fact]
    public void Manifest_round_trips()
    {
        var manifest = SampleManifest();
        var result = BackupManifestReader.Parse(BackupManifestReader.Serialize(manifest));

        Assert.Null(result.Error);
        Assert.Equal(manifest.ArchiveId, result.Manifest!.ArchiveId);
        Assert.Equal(manifest.Totals, result.Manifest.Totals);
    }

    [Fact]
    public void Unsupported_major_version_is_rejected_with_a_clear_message()
    {
        var result = BackupManifestReader.Parse(BackupManifestReader.Serialize(SampleManifest(major: 2)));

        Assert.Null(result.Manifest);
        Assert.Contains("format 2.x", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"formatVersion\":{\"major\":\"one\"}}")]
    [InlineData("{\"formatVersion\":{\"major\":1}}")]
    public void Malformed_manifests_are_rejected(string json)
        => Assert.Null(BackupManifestReader.Parse(json).Manifest);

    [Fact]
    public void Secrets_never_appear_in_text_or_record_printing()
    {
        var secret = new SecretText("hunter2-super-secret");
        var request = new ArchiveCreateRequest("C:\\in", "C:\\out.devbr", secret, CompressionPreset.Normal, false);

        Assert.Equal("[redacted]", secret.ToString());
        Assert.DoesNotContain("hunter2", $"{secret}", StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", request.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_survive_the_in_memory_ipc_serializer()
    {
        var request = new ArchiveInspectRequest("C:\\b.devbr", new SecretText("pw ✓"), [], 0);
        var json = JsonSerializer.Serialize(request, IpcJson.Options);
        var back = JsonSerializer.Deserialize<ArchiveInspectRequest>(json, IpcJson.Options)!;

        Assert.Equal("pw ✓", back.Password!.Reveal());
    }
}
