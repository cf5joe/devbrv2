using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Archive;
using DevBR.Backup;
using DevBR.Discovery;
using DevBR.Domain;
using DevBR.Simulation;
using Microsoft.Extensions.Logging.Abstractions;
using SharpSevenZip;

namespace DevBR.Tests.Backup;

/// <summary>Builds the sample workstation and its discovery snapshot once for all backup tests.</summary>
public sealed class BackupFixture : IAsyncLifetime
{
    public TempDirectory Temp { get; } = new();

    public SimulatedMachine Machine { get; private set; } = null!;

    public DiscoverySnapshot Snapshot { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Machine = SampleMachines.DeveloperWorkstation(Temp.Combine("machine"));
        Snapshot = await new DiscoveryEngine(DiscoveryEngine.DefaultProviders(), NullLogger<DiscoveryEngine>.Instance)
            .RunAsync(Machine, DiscoveryOptions.Default, null, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        Temp.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class BackupTests(BackupFixture fixture) : IClassFixture<BackupFixture>, IDisposable
{
    private readonly TempDirectory _out = new();
    private readonly SevenZipArchiveService _archive = new(Loggers.For<SevenZipArchiveService>());

    public void Dispose() => _out.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MigrationArtifact Artifact(string id) => fixture.Snapshot.Artifacts.Single(a => a.Id == id);

    private MigrationArtifact Repository(string name) => fixture.Snapshot.Artifacts.Single(a => a.Kind == ArtifactKind.Repository && a.DisplayName.StartsWith(name + " (", StringComparison.Ordinal));

    private BackupPlan Plan(IEnumerable<MigrationArtifact> selected, IReadOnlyList<string>? custom = null, IReadOnlyList<string>? protectedPaths = null)
        => new BackupPlanner().Plan(new BackupPlanRequest(fixture.Machine, fixture.Snapshot, [.. selected], custom ?? [], DefaultExclusions.All,
            protectedPaths ?? [_out.Path]), null, Ct);

    private Task<BackupResult> RunAsync(BackupPlan plan, string name, SecretText? password = null, bool overwrite = false, CancellationToken? token = null)
        => new BackupRunner(_archive, NullLogger<BackupRunner>.Instance).RunAsync(plan,
            new BackupRunOptions(_out.Combine(name), _out.Combine("scratch"), CompressionPreset.Fast, password, overwrite), null, token ?? Ct);

    private IEnumerable<MigrationArtifact> Baseline => fixture.Snapshot.Artifacts.Where(a => a.SelectedByDefault);

    private string ExtractPayload(BackupOverview overview, SecretText? password, string archiveRelative)
    {
        var target = _out.Combine("extract", Guid.NewGuid().ToString("N"));
        _archive.ExtractSelectedAsync(new ArchiveExtractRequest(overview.ArchivePath, password, target, [archiveRelative]), null, Ct).GetAwaiter().GetResult();
        return File.ReadAllText(Path.Combine(target, archiveRelative.Replace('/', '\\')));
    }

    private static string SourceHash(SimulatedMachine machine, string path)
    {
        using var stream = machine.FileSystem.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // --- One self-contained, verified file --------------------------------------------------------

    [Fact]
    public async Task Baseline_backup_is_one_verified_file_with_indexes_and_inventory()
    {
        var plan = Plan(Baseline);
        var result = await RunAsync(plan, "baseline.devbr");

        Assert.Equal(BackupOutcome.Succeeded, result.Outcome);
        Assert.True(result.Verified);
        Assert.Single(Directory.GetFiles(_out.Path, "*.devbr"));
        Assert.False(Directory.Exists(_out.Combine("scratch")) && Directory.EnumerateDirectories(_out.Combine("scratch")).Any(), "staging must be removed");

        var overview = await new BackupReader(_archive).OpenAsync(result.OutputPath!, null, _out.Combine("scratch"), Ct);
        try
        {
            Assert.Equal("ALICE-DEV", overview.Manifest.SourceMachineName);
            Assert.False(overview.Encrypted);
            Assert.True(overview.InventoryCount > 30);
            Assert.Equal(BackupRunner.Disclosure, overview.Report!.Disclosure);

            var environment = overview.Artifacts.Single(a => a.Record.Artifact.Id == "environment:machine");
            var json = ExtractPayload(overview, null, $"payload/{environment.Record.Key}/environment.json");
            Assert.Contains("DOTNET_CLI_TELEMETRY_OPTOUT", json, StringComparison.Ordinal);
            Assert.Contains("ExpandString", json, StringComparison.Ordinal); // REG_EXPAND_SZ preserved
            Assert.DoesNotContain("GITHUB_TOKEN", json, StringComparison.Ordinal);
        }
        finally
        {
            BackupReader.Close(overview);
        }
    }

    [Fact]
    public async Task Round_trip_preserves_selected_bytes_and_metadata()
    {
        var plan = Plan([Artifact("vscode:default:settings"), Artifact("vscode:default:snippets"), Artifact("git:config"), Artifact("powershell:ps7:profiles")]);
        var result = await RunAsync(plan, "config.devbr");
        Assert.True(result.Verified);

        var overview = await new BackupReader(_archive).OpenAsync(result.OutputPath!, null, _out.Combine("scratch"), Ct);
        try
        {
            var all = overview.Artifacts.SelectMany(a => BackupReader.ReadEntries(overview, a.Record.Key, 0, 1000).Select(e => (a, e))).ToList();
            Assert.Equal(result.Files, all.Count(x => x.e.EntryType == ArchiveEntryType.File));

            foreach (var (artifact, entry) in all.Where(x => x.e.EntryType == ArchiveEntryType.File))
            {
                var root = artifact.Record.Roots.Single(r => entry.ArchivePath.StartsWith($"payload/{artifact.Record.Key}/r{r.Index}/", StringComparison.Ordinal));
                var source = entry.RelativePath.Length == 0 ? root.SourcePath : Path.Combine(root.SourcePath, entry.RelativePath);
                Assert.Equal(SourceHash(fixture.Machine, source), entry.Sha256);
            }

            // The referenced script travels with the profile; the included git config with the global one.
            Assert.Contains(all, x => x.e.ArchivePath.EndsWith("aliases.ps1", StringComparison.Ordinal));
            Assert.Contains(all, x => x.e.ArchivePath.EndsWith(".gitconfig-work", StringComparison.Ordinal));
        }
        finally
        {
            BackupReader.Close(overview);
        }
    }

    // --- Encryption -------------------------------------------------------------------------------

    [Fact]
    public async Task Encrypted_backup_conceals_names_and_requires_the_password()
    {
        var password = new SecretText("correct horse battery staple");
        var result = await RunAsync(Plan(Baseline.Append(Artifact("claude-code:settings"))), "encrypted.devbr", password);
        Assert.True(result.Verified);
        Assert.True(result.Encrypted);

        var raw = Encoding.Unicode.GetString(await File.ReadAllBytesAsync(result.OutputPath!, Ct));
        Assert.DoesNotContain("manifest.json", raw, StringComparison.Ordinal);

        var reader = new BackupReader(_archive);
        Assert.Equal(ArchiveErrorKind.PasswordRequired,
            (await Assert.ThrowsAsync<ArchiveException>(() => reader.OpenAsync(result.OutputPath!, null, _out.Combine("scratch"), Ct))).Kind);
        Assert.Equal(ArchiveErrorKind.WrongPasswordOrCorrupt,
            (await Assert.ThrowsAsync<ArchiveException>(() => reader.OpenAsync(result.OutputPath!, new SecretText("wrong"), _out.Combine("scratch"), Ct))).Kind);

        var overview = await reader.OpenAsync(result.OutputPath!, password, _out.Combine("scratch"), Ct);
        Assert.True(overview.Encrypted);
        BackupReader.Close(overview);
    }

    [Fact]
    public async Task Selected_credentials_force_encryption()
    {
        var plan = Plan(Baseline.Append(Artifact("environment:secret:user:GITHUB_TOKEN")));
        Assert.True(plan.RequiresEncryption);

        var refused = await RunAsync(plan, "secret.devbr");
        Assert.Equal(BackupOutcome.EncryptionRequired, refused.Outcome);
        Assert.False(File.Exists(_out.Combine("secret.devbr")));

        var password = new SecretText("long enough password");
        var result = await RunAsync(plan, "secret.devbr", password);
        Assert.True(result.Verified);

        var overview = await new BackupReader(_archive).OpenAsync(result.OutputPath!, password, _out.Combine("scratch"), Ct);
        var secret = overview.Artifacts.Single(a => a.Record.Artifact.Id == "environment:secret:user:GITHUB_TOKEN");
        Assert.Contains("ghp_SIMULATED", ExtractPayload(overview, password, $"payload/{secret.Record.Key}/environment.json"), StringComparison.Ordinal);
        BackupReader.Close(overview);
    }

    [Fact]
    public async Task Secrets_detected_in_selected_content_force_encryption()
    {
        var folder = fixture.Machine.MapPath(@"D:\Notes");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "deploy.txt"), "key = sk-ant-api03-SIMULATEDsimulatedSIMULATEDxyz", Ct);

        var plan = Plan(Baseline, custom: [@"D:\Notes"]);
        Assert.False(plan.RequiresEncryption); // only detectable by reading the content

        var result = await RunAsync(plan, "notes.devbr");
        Assert.Equal(BackupOutcome.EncryptionRequired, result.Outcome);
        Assert.Contains(@"D:\Notes\deploy.txt", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", result.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(_out.Combine("notes.devbr")));
    }

    [Fact]
    public async Task Structured_redaction_removes_account_state_trust_and_registry_credentials()
    {
        var plan = Plan([Artifact("claude-code:user-mcp"), Artifact("copilot:settings"), Artifact("docker:client-config")]);
        Assert.False(plan.RequiresEncryption); // the transforms remove the recognized secrets

        var result = await RunAsync(plan, "redacted.devbr");
        var overview = await new BackupReader(_archive).OpenAsync(result.OutputPath!, null, _out.Combine("scratch"), Ct);
        try
        {
            string Payload(string id)
            {
                var artifact = overview.Artifacts.Single(a => a.Record.Artifact.Id == id);
                var entry = BackupReader.ReadEntries(overview, artifact.Record.Key, 0, 10).Single();
                return ExtractPayload(overview, null, entry.ArchivePath);
            }

            var claude = Payload("claude-code:user-mcp");
            Assert.Contains("mcpServers", claude, StringComparison.Ordinal);
            Assert.DoesNotContain("oauthAccount", claude, StringComparison.Ordinal);
            Assert.DoesNotContain("alice@example.com", claude, StringComparison.Ordinal);

            Assert.DoesNotContain("trusted_folders", Payload("copilot:settings"), StringComparison.Ordinal);

            var docker = Payload("docker:client-config");
            Assert.DoesNotContain("YWxpY2U6c2ltdWxhdGVk", docker, StringComparison.Ordinal);
            Assert.Contains("credsStore", docker, StringComparison.Ordinal);
        }
        finally
        {
            BackupReader.Close(overview);
        }
    }

    // --- Repositories -----------------------------------------------------------------------------

    [Fact]
    public async Task Repository_capture_keeps_git_data_and_tracked_files_and_excludes_regenerable_folders()
    {
        var plan = Plan([Repository("webapp")]);
        var webapp = plan.Artifacts.Single();

        Assert.Contains(webapp.Exclusions, e => e.Rule == "node_modules");
        Assert.Contains(webapp.Exclusions, e => e.Rule == "obj");
        Assert.DoesNotContain(webapp.Exclusions, e => e.Rule == "bin");
        Assert.Contains(plan.Findings, f => f.Message.Contains("contains tracked files, so it is kept", StringComparison.Ordinal));

        var files = webapp.Files.Select(f => f.RelativePath).ToList();
        Assert.Contains(@".git\HEAD", files);
        Assert.Contains(@".git\index", files);
        Assert.Contains(@"bin\build.sh", files);         // tracked: kept despite the "bin" rule
        Assert.Contains("notes.local.md", files);        // untracked but not excluded: kept
        Assert.DoesNotContain(files, f => f.StartsWith("node_modules", StringComparison.Ordinal) || f.StartsWith("obj", StringComparison.Ordinal));

        var result = await RunAsync(plan, "repo.devbr");
        Assert.True(result.Verified);
        Assert.Equal("Complete", result.Artifacts.Single().Status);
    }

    [Fact]
    public void Repositories_with_active_locks_are_blocked_with_remediation()
    {
        var plan = Plan([Repository("scratch")]);
        var finding = Assert.Single(plan.Findings, f => f.Level == FindingLevel.Blocking);
        Assert.Contains("Git operation is in progress", finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.Remediation);
        Assert.True(plan.Artifacts.Single().Blocked);
        Assert.Empty(plan.Artifacts.Single().Files);
    }

    [Fact]
    public void Missing_external_git_storage_is_reported()
    {
        var worktree = Plan([Repository("api-hotfix")]);
        Assert.Contains(worktree.Findings, f => f.Level == FindingLevel.Warning && f.Message.Contains("not selected", StringComparison.Ordinal));

        var withOwner = Plan([Repository("api-hotfix"), Repository("api")]);
        Assert.DoesNotContain(withOwner.Findings, f => f.Message.Contains("not selected", StringComparison.Ordinal));

        var alternates = Plan([Repository("monorepo-fork")]);
        Assert.Contains(alternates.Findings, f => f.Message.Contains("borrows Git objects", StringComparison.Ordinal));
        Assert.Contains(Plan([Repository("api")]).Findings, f => f.Message.Contains("Git LFS", StringComparison.Ordinal));
    }

    // --- Overlaps and protected paths -------------------------------------------------------------

    [Fact]
    public void Overlapping_selections_capture_each_file_once_and_never_capture_devbr_output()
    {
        var plan = Plan([Repository("webapp")], custom: [@"D:\Projects"], protectedPaths: [@"D:\Projects\api"]);
        var all = plan.Artifacts.SelectMany(a => a.Files.Where(f => !f.IsDirectory)).Select(f => f.SourcePath).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(all, p => p.StartsWith(@"D:\Projects\api\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Findings, f => f.Message.Contains("DevBR's own output", StringComparison.Ordinal));
    }

    // --- Accurate outcomes ------------------------------------------------------------------------

    [Fact]
    public async Task Locked_files_are_reported_and_the_rest_is_captured()
    {
        var result = await RunAsync(Plan([Artifact("docker:desktop-settings"), Artifact("gemini-cli:settings")]), "locked.devbr");

        Assert.Equal(BackupOutcome.SucceededWithWarnings, result.Outcome);
        var docker = result.Artifacts.Single(a => a.ArtifactId == "docker:desktop-settings");
        Assert.Equal("Incomplete", docker.Status);
        Assert.Contains(docker.Warnings, w => w.Contains("in use by another program", StringComparison.Ordinal));
        Assert.Equal("Complete", result.Artifacts.Single(a => a.ArtifactId == "gemini-cli:settings").Status);
        Assert.True(result.Verified);
    }

    [Fact]
    public async Task Existing_output_is_never_replaced_without_an_explicit_decision()
    {
        var target = _out.Combine("existing.devbr");
        await File.WriteAllTextAsync(target, "keep me", Ct);

        var refused = await RunAsync(Plan(Baseline), "existing.devbr");
        Assert.Equal(ArchiveErrorKind.OutputExists, refused.ErrorKind);
        Assert.Equal("keep me", await File.ReadAllTextAsync(target, Ct));

        var replaced = await RunAsync(Plan(Baseline), "existing.devbr", overwrite: true);
        Assert.True(replaced.Verified);
    }

    [Fact]
    public async Task Cancellation_leaves_no_output_and_no_staging()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await RunAsync(Plan([Repository("webapp")]), "cancelled.devbr", token: cts.Token);

        Assert.Equal(BackupOutcome.Cancelled, result.Outcome);
        Assert.False(File.Exists(_out.Combine("cancelled.devbr")));
        Assert.Empty(Directory.GetFiles(_out.Path, "*.partial-*"));
        Assert.False(Directory.Exists(_out.Combine("scratch")) && Directory.EnumerateFileSystemEntries(_out.Combine("scratch")).Any());
    }

    [Fact]
    public async Task Insufficient_space_is_detected_before_anything_is_written()
    {
        var plan = Plan(Baseline);
        var huge = plan with
        {
            Artifacts = [.. plan.Artifacts, plan.Artifacts[0] with { Key = "a9999", Kind = CaptureKind.Files, Files = [new PlannedFile(0, @"C:\huge.bin", "", long.MaxValue / 8, DateTimeOffset.UtcNow, false)] }],
        };

        var result = await RunAsync(huge, "huge.devbr");
        Assert.Equal(ArchiveErrorKind.InsufficientSpace, result.ErrorKind);
        Assert.False(Directory.Exists(_out.Combine("scratch")) && Directory.EnumerateFileSystemEntries(_out.Combine("scratch")).Any());
    }

    [Fact]
    public async Task Corrupt_archives_are_reported_not_trusted()
    {
        var result = await RunAsync(Plan(Baseline.Append(Repository("webapp"))), "corrupt.devbr");
        var bytes = await File.ReadAllBytesAsync(result.OutputPath!, Ct);
        for (var i = bytes.Length / 3; i < bytes.Length / 3 + 64; i++)
        {
            bytes[i] ^= 0xFF;
        }

        var damaged = _out.Combine("damaged.devbr");
        await File.WriteAllBytesAsync(damaged, bytes, Ct);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => new BackupReader(_archive).OpenAsync(damaged, null, _out.Combine("scratch"), Ct));
        Assert.True(error is ArchiveException or BackupFormatException, error.GetType().Name);
    }

    [Fact]
    public async Task Altered_indexes_and_unsafe_entry_paths_are_rejected()
    {
        var result = await RunAsync(Plan(Baseline), "original.devbr");
        var unpacked = _out.Combine("unpacked");
        await _archive.ExtractSelectedAsync(new ArchiveExtractRequest(result.OutputPath!, null, unpacked, null), null, Ct);
        var entries = Path.Combine(unpacked, "entries.ndjson");

        // 1. Index changed but manifest hash left alone → mismatch.
        await File.AppendAllTextAsync(entries, "\n", Ct);
        Assert.Contains("does not match the manifest", (await Assert.ThrowsAsync<BackupFormatException>(() => OpenRepacked("altered.devbr"))).Message, StringComparison.Ordinal);

        // 2. Hash fixed up, but an entry escapes the payload folder → rejected as unsafe.
        var lines = (await File.ReadAllLinesAsync(entries, Ct)).Where(l => l.Length > 0).ToList();
        lines[0] = lines[0].Replace("\"archivePath\":\"payload/", "\"archivePath\":\"../payload/", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(entries, lines, Ct);
        var manifestPath = Path.Combine(unpacked, "manifest.json");
        var manifest = BackupManifestReader.Parse(await File.ReadAllTextAsync(manifestPath, Ct)).Manifest!;
        var hashes = new Dictionary<string, string>(manifest.IndexSha256) { ["entries.ndjson"] = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(entries, Ct))) };
        await File.WriteAllTextAsync(manifestPath, BackupManifestReader.Serialize(manifest with { IndexSha256 = hashes }), Ct);
        Assert.Contains("unsafe path", (await Assert.ThrowsAsync<BackupFormatException>(() => OpenRepacked("unsafe.devbr"))).Message, StringComparison.Ordinal);

        async Task<BackupOverview> OpenRepacked(string name)
        {
            var path = _out.Combine(name);
            new SharpSevenZipCompressor { ArchiveFormat = OutArchiveFormat.SevenZip, IncludeEmptyDirectories = true }.CompressDirectory(unpacked, path);
            return await new BackupReader(_archive).OpenAsync(path, null, _out.Combine("scratch"), Ct);
        }
    }

    [Fact]
    public async Task Manifest_and_report_never_contain_secret_values()
    {
        var plan = Plan(Baseline.Append(Artifact("environment:user")));
        var result = await RunAsync(plan, "nosecrets.devbr");
        var overview = await new BackupReader(_archive).OpenAsync(result.OutputPath!, null, _out.Combine("scratch"), Ct);
        try
        {
            foreach (var file in Directory.GetFiles(overview.CatalogFolder, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain("ghp_SIMULATED", File.ReadAllText(file), StringComparison.Ordinal);
            }

            var user = overview.Artifacts.Single(a => a.Record.Artifact.Id == "environment:user");
            var json = ExtractPayload(overview, null, $"payload/{user.Record.Key}/environment.json");
            Assert.Contains("PROJECTS", json, StringComparison.Ordinal);
            Assert.DoesNotContain("GITHUB_TOKEN", json, StringComparison.Ordinal);
            Assert.Equal(2, JsonDocument.Parse(json).RootElement.EnumerateArray().Count(v => v.GetProperty("name").GetString() is "Path" or "PROJECTS"));
        }
        finally
        {
            BackupReader.Close(overview);
        }
    }
}
