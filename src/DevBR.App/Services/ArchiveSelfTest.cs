using System.IO;
using System.Security.Cryptography;
using DevBR.Application;
using DevBR.Application.Archive;

namespace DevBR.App.Services;

public sealed record SelfTestStep(string Name, bool Passed, string Detail)
{
    // Lists without an item container announce ToString(); keep it a readable name, never a record dump.
    public override string ToString() => $"{Name}: {(Passed ? "pass" : "fail")}, {Detail}";
}

/// <summary>
/// Exercises the archive worker end to end on this machine: Unicode paths, empty folders, a multi-MB
/// file, non-solid output, encrypted headers, and password handling. Works only in a private folder
/// under the scratch directory and removes it afterwards.
/// </summary>
public sealed class ArchiveSelfTest(IArchiveService archive)
{
    public async Task<IReadOnlyList<SelfTestStep>> RunAsync(string scratchRoot, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var steps = new List<SelfTestStep>();
        var root = Path.Combine(scratchRoot, $"selftest-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "source");

        try
        {
            progress?.Report("Preparing sample files");
            var expected = CreateSampleTree(source);

            await RoundTripAsync("Unencrypted", null);
            await RoundTripAsync("Encrypted", new SecretText(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))));

            return steps;

            async Task RoundTripAsync(string label, SecretText? password)
            {
                var archivePath = Path.Combine(root, $"{label.ToLowerInvariant()}.devbr");
                var output = Path.Combine(root, $"{label.ToLowerInvariant()}-out");

                progress?.Report($"{label}: creating archive");
                var created = await archive.CreateAsync(new ArchiveCreateRequest(source, archivePath, password, CompressionPreset.Fast, AllowOverwrite: false), null, cancellationToken);
                steps.Add(new SelfTestStep($"{label}: create and verify", created.Verified, $"{created.FileCount} files, {Formatting.Bytes(created.ArchiveBytes)} on disk"));

                var refused = await ExpectArchiveErrorAsync(() => archive.CreateAsync(new ArchiveCreateRequest(source, archivePath, password, CompressionPreset.Fast, AllowOverwrite: false), null, cancellationToken), ArchiveErrorKind.OutputExists);
                steps.Add(new SelfTestStep($"{label}: existing output is not replaced", refused, refused ? "Refused without an overwrite decision" : "Existing file was not protected"));

                if (password is not null)
                {
                    var hidden = await ExpectArchiveErrorAsync(() => archive.InspectAsync(new ArchiveInspectRequest(archivePath, null, [], 0), cancellationToken), ArchiveErrorKind.PasswordRequired);
                    steps.Add(new SelfTestStep($"{label}: file names hidden without password", hidden, hidden ? "Listing required the password" : "Entries were readable without a password"));

                    var wrong = await ExpectArchiveErrorAsync(() => archive.InspectAsync(new ArchiveInspectRequest(archivePath, new SecretText("not-the-password"), [], 0), cancellationToken), ArchiveErrorKind.WrongPasswordOrCorrupt);
                    steps.Add(new SelfTestStep($"{label}: wrong password rejected", wrong, wrong ? "Rejected" : "A wrong password was accepted"));
                }

                progress?.Report($"{label}: inspecting");
                var inspection = await archive.InspectAsync(new ArchiveInspectRequest(archivePath, password, [], 0), cancellationToken);
                steps.Add(new SelfTestStep($"{label}: non-solid layout", !inspection.IsSolid, inspection.IsSolid ? "Archive is solid" : "Entries are independently readable"));

                progress?.Report($"{label}: extracting");
                var extracted = await archive.ExtractSelectedAsync(new ArchiveExtractRequest(archivePath, password, output, null), null, cancellationToken);
                var mismatches = expected.Count(pair => !extracted.Files.Any(f =>
                    string.Equals(f.ArchivePath, pair.Key, StringComparison.OrdinalIgnoreCase) && f.Sha256 == pair.Value));
                var emptyDirectory = Directory.Exists(Path.Combine(output, "empty folder"));
                steps.Add(new SelfTestStep($"{label}: extracted bytes match", mismatches == 0 && emptyDirectory,
                    mismatches == 0 ? (emptyDirectory ? "All SHA-256 hashes match; empty folder kept" : "Empty folder was lost") : $"{mismatches} files differ"));
            }
        }
        catch (ArchiveException ex)
        {
            steps.Add(new SelfTestStep("Archive operation", false, $"{ex.Kind}: {ex.Message}"));
            return steps;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<bool> ExpectArchiveErrorAsync(Func<Task> action, ArchiveErrorKind kind)
    {
        try
        {
            await action();
            return false;
        }
        catch (ArchiveException ex)
        {
            return ex.Kind == kind;
        }
    }

    /// <returns>Relative path → SHA-256 for every file created.</returns>
    private static Dictionary<string, string> CreateSampleTree(string source)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["manifest.json"] = "{\"sample\":true}"u8.ToArray(),
            [Path.Combine("payload", "unicode", "Привет мир — 世界 ✓.txt")] = "unicode name"u8.ToArray(),
            [Path.Combine("payload", "spaces in name", "notes with spaces.md")] = "# notes"u8.ToArray(),
            [Path.Combine("payload", "empty-file.txt")] = [],
            [Path.Combine("payload", "large.bin")] = RandomNumberGenerator.GetBytes(8 * 1024 * 1024),
        };

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            hashes[relative] = Convert.ToHexStringLower(SHA256.HashData(content));
        }

        Directory.CreateDirectory(Path.Combine(source, "empty folder"));
        return hashes;
    }
}
