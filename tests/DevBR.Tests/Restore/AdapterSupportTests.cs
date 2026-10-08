using DevBR.Application.Archive;
using DevBR.Application.Machine;
using DevBR.Archive;
using DevBR.Backup;
using DevBR.Discovery;
using DevBR.Discovery.Adapters;
using DevBR.Domain;
using DevBR.Restore;
using DevBR.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Tests.Restore;

/// <summary>
/// One supported-version and two unverified-version (outdated, unreadable) fixtures per adapter: what discovery
/// finds, and whether restore plans a structured merge with path rewrites or falls back to whole-file handling.
/// </summary>
public sealed class AdapterSupportTests(AdapterSupportFixture fixture) : IClassFixture<AdapterSupportFixture>
{
    public static TheoryData<string> Adapters => new(AdapterCase.All.Select(c => c.AdapterId));

    public static TheoryData<string, HostVersions> UnverifiedCases
    {
        get
        {
            var data = new TheoryData<string, HostVersions>();
            foreach (var c in AdapterCase.All)
            {
                data.Add(c.AdapterId, HostVersions.Outdated);
                data.Add(c.AdapterId, HostVersions.Unknown);
            }

            return data;
        }
    }

    private static IToolAdapter Adapter(string id) => Assert.Single(DiscoveryEngine.DefaultAdapters, a => a.Id == id);

    [Fact]
    public void Every_adapter_has_a_fixture_and_a_complete_descriptor()
    {
        Assert.Equal(DiscoveryEngine.DefaultAdapters.Select(a => a.Id).Order(), AdapterCase.All.Select(c => c.AdapterId).Order());
        Assert.All(DiscoveryEngine.DefaultAdapters, a =>
        {
            Assert.NotEmpty(a.Support.VerifiedVersions);
            Assert.NotEmpty(a.Support.Locations);
            Assert.NotEmpty(a.Support.Prerequisites);
            Assert.True(a.Support.Capabilities.HasFlag(AdapterCapabilities.Capture));
            Assert.All(a.Support.VerifiedVersions, r => AdapterVersions.Parse(r.Minimum));
        });
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void Fixture_versions_match_the_descriptor(string adapterId)
    {
        var c = AdapterCase.Get(adapterId);
        var support = Adapter(adapterId).Support;
        Assert.Equal(c.ToolId, support.HostToolId);
        Assert.Equal(VersionSupport.Supported, support.Evaluate(c.Supported));
        Assert.Equal(VersionSupport.Unsupported, support.Evaluate(c.Outdated));
        Assert.Equal(VersionSupport.Unknown, support.Evaluate(null));
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void Discovery_finds_the_host_version_and_captures_configuration(string adapterId)
    {
        var c = AdapterCase.Get(adapterId);
        foreach (var (mode, snapshot) in fixture.Snapshots)
        {
            var artifact = Assert.Single(snapshot.Artifacts, a => a.Id == c.ArtifactId);
            Assert.Equal(BackupEligibility.Eligible, artifact.Eligibility);

            var version = c.ToolId == "windows"
                ? snapshot.Items.Single(i => i.Category == InventoryCategory.SystemFact && i.Properties?.ContainsKey("architecture") == true).Version
                : snapshot.Items.Where(i => i.ToolId == c.ToolId && i.Category is InventoryCategory.DeveloperTool or InventoryCategory.Runtime).Select(i => i.Version).FirstOrDefault(v => v is not null);
            var expected = mode switch
            {
                HostVersions.Supported => VersionSupport.Supported,
                HostVersions.Outdated => VersionSupport.Unsupported,
                _ => VersionSupport.Unknown,
            };
            Assert.Equal(expected, Adapter(adapterId).Support.Evaluate(version));
        }
    }

    [Theory]
    [MemberData(nameof(Adapters))]
    public void Supported_versions_get_semantic_handling(string adapterId)
    {
        var c = AdapterCase.Get(adapterId);
        var support = Adapter(adapterId).Support;
        var preflight = fixture.Preflights[HostVersions.Supported];
        var ops = preflight.Operations.Where(o => o.Operation.ArtifactId == c.ArtifactId).ToList();

        Assert.DoesNotContain(preflight.Findings, f => f.Id == $"unverified-version:{c.ToolId}");
        Assert.DoesNotContain(preflight.BlockedArtifacts, a => a == c.ArtifactId);
        if (c.ToolId == "windows")
        {
            Assert.Contains(ops, o => o.Operation.Action == RestoreAction.SetEnvironmentVariable && o.Operation.Target == "ENV:User:PROJECTS");
            Assert.Contains(preflight.Rewrites, r => r.ArtifactId == c.ArtifactId && r.Outcome == RewriteOutcome.Rewritten);
        }
        else if (support.HasSemanticHandling)
        {
            var merge = Assert.Single(ops, o => o.Operation.Action == RestoreAction.MergeStructuredSettings);
            Assert.Contains(merge.Merge!.Changes, ch => ch.Kind == MergeChangeKind.Added);
            Assert.False(merge.Verbatim);
            Assert.Contains(preflight.Rewrites, r => r.ArtifactId == c.ArtifactId && r.Outcome == RewriteOutcome.Rewritten && r.After.Contains(@"\Users\alex\", StringComparison.Ordinal));
        }
        else
        {
            AssertWholeFile(ops, verbatim: false);
        }
    }

    [Theory]
    [MemberData(nameof(UnverifiedCases))]
    public void Unverified_versions_fall_back_with_a_specific_finding(string adapterId, HostVersions mode)
    {
        var c = AdapterCase.Get(adapterId);
        var support = Adapter(adapterId).Support;
        var preflight = fixture.Preflights[mode];
        var ops = preflight.Operations.Where(o => o.Operation.ArtifactId == c.ArtifactId).ToList();

        Assert.DoesNotContain(ops, o => o.Operation.Action == RestoreAction.MergeStructuredSettings || o.Merge is not null);
        Assert.DoesNotContain(preflight.Rewrites, r => r.ArtifactId == c.ArtifactId);

        if (!support.HasSemanticHandling)
        {
            // Nothing semantic to give up: the same whole-file handling as for verified versions, and no finding.
            AssertWholeFile(ops, verbatim: false);
            Assert.DoesNotContain(preflight.Findings, f => f.Id == $"unverified-version:{c.ToolId}");
            return;
        }

        var finding = Assert.Single(preflight.Findings, f => f.Id == $"unverified-version:{c.ToolId}");
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Contains(c.ArtifactId, finding.AffectedArtifactIds);
        Assert.Contains(support.HostName, finding.Problem, StringComparison.Ordinal);
        Assert.Contains(mode == HostVersions.Outdated ? $"{c.Outdated} on this computer is not a version DevBR has verified" : "could not determine which version", finding.Problem, StringComparison.Ordinal);
        Assert.Contains(support.VersionText, string.Join(" ", finding.NextSteps.Append(finding.Problem)), StringComparison.Ordinal);

        if (c.ToolId == "windows")
        {
            Assert.Empty(ops);
            Assert.Contains(preflight.Findings, f => f.AffectedArtifactIds.Contains(c.ArtifactId) && f.Problem.Contains("PROJECTS (user)", StringComparison.Ordinal));
        }
        else
        {
            AssertWholeFile(ops, verbatim: true);
        }
    }

    [Fact]
    public async Task Replacing_an_unverified_file_writes_it_exactly_as_captured()
    {
        var c = AdapterCase.Get("vscode");
        var target = fixture.Target("replace", HostVersions.Outdated);
        var keep = await fixture.PreflightAsync(target, null, TestContext.Current.CancellationToken);
        var op = Assert.Single(keep.Operations, o => o.Operation.ArtifactId == c.ArtifactId && o.Operation.Action == RestoreAction.Skip);

        var replace = await fixture.PreflightAsync(target, new Dictionary<string, ConflictDecision> { [op.Operation.Id] = ConflictDecision.UseBackup }, TestContext.Current.CancellationToken);
        var replaced = Assert.Single(replace.Operations, o => o.Operation.Id == op.Operation.Id);
        Assert.Equal(RestoreAction.ReplaceFile, replaced.Operation.Action);
        Assert.True(replaced.Verbatim);
        Assert.DoesNotContain(replace.Rewrites, r => r.ArtifactId == c.ArtifactId);
    }

    private static void AssertWholeFile(List<PlannedOperation> ops, bool verbatim)
    {
        var op = Assert.Single(ops, o => o.Operation.Action != RestoreAction.ManualStep);
        Assert.Equal(RestoreAction.Skip, op.Operation.Action);
        Assert.Equal(ConflictDecision.KeepExisting, op.Operation.ConflictDecision);
        Assert.Equal([ConflictDecision.KeepExisting, ConflictDecision.UseBackup, ConflictDecision.RestoreAlongside], op.AllowedDecisions);
        Assert.Equal(verbatim, op.Verbatim);
    }
}

public enum HostVersions
{
    Supported,
    Outdated,
    Unknown,
}

/// <summary>One adapter's fixture: its host, a configuration file on the old computer and a different one on the new.</summary>
/// <param name="Executable">Host executable placed on PATH (null: WSL, registered through Add/Remove Programs; Windows: the OS build).</param>
/// <param name="Path">Configuration file under the user profile (environment: unused).</param>
public sealed record AdapterCase(string AdapterId, string ArtifactId, string ToolId, string? Executable, string Supported, string Outdated, string Path, string Backup, string Existing)
{
    private const string Mcp = """{ "mcpServers": { "docs": { "command": "npx", "args": ["docs-mcp"], "cwd": "C:\\Users\\alice\\work" } } }""";
    private const string OtherMcp = """{ "mcpServers": { "other": { "command": "npx", "args": ["other"] } } }""";
    private const string Editor = """{ "editor.fontSize": 14, "python.defaultInterpreterPath": "C:\\Users\\alice\\py\\python.exe" }""";
    private const string OtherEditor = """{ "editor.fontSize": 16 }""";

    public static IReadOnlyList<AdapterCase> All { get; } =
    [
        new("environment", "environment:user", "windows", null, "26100", "10586", string.Empty, string.Empty, string.Empty),
        new("vscode", "vscode:default:settings", "vscode", "Code.exe", "1.105.0", "1.80.0", @"AppData\Roaming\Code\User\settings.json", Editor, OtherEditor),
        new("vscode-insiders", "vscode-insiders:default:settings", "vscode-insiders", "Code - Insiders.exe", "1.106.0", "1.85.0", @"AppData\Roaming\Code - Insiders\User\settings.json", Editor, OtherEditor),
        new("cursor", "cursor:settings", "cursor", "Cursor.exe", "1.7.0", "0.50.0", @"AppData\Roaming\Cursor\User\settings.json", Editor, OtherEditor),
        new("copilot", "copilot:mcp", "copilot-cli", "copilot.exe", "0.0.340", "0.0.300", @".copilot\mcp-config.json", Mcp, OtherMcp),
        new("codex", "codex:config", "codex", "codex.exe", "0.50.0", "0.30.0", @".codex\config.toml", "model = \"gpt-5-codex\"\n", "model = \"o3\"\n"),
        new("claude-code", "claude-code:settings", "claude-code", "claude.exe", "2.1.0", "0.2.0", @".claude\settings.json",
            """{ "permissions": { "additionalDirectories": ["C:\\Users\\alice\\shared"] } }""", """{ "theme": "dark" }"""),
        new("claude-desktop", "claude-desktop:config", "claude-desktop", "Claude.exe", "1.0.211", "0.9.0", @"AppData\Roaming\Claude\claude_desktop_config.json", Mcp, OtherMcp),
        new("gemini-cli", "gemini-cli:settings", "gemini-cli", "gemini.exe", "0.9.0", "0.4.0", @".gemini\settings.json", Mcp, OtherMcp),
        new("git", "git:config", "git", "git.exe", "2.55.0", "2.20.0", ".gitconfig", "[user]\n\tname = Alice\n", "[user]\n\tname = Alex\n"),
        new("gh", "gh:config", "gh", "gh.exe", "2.80.0", "2.30.0", @"AppData\Roaming\GitHub CLI\config.yml", "git_protocol: https\n", "git_protocol: ssh\n"),
        new("powershell", "powershell:ps7:profiles", "pwsh", "pwsh.exe", "7.5.3", "7.0.0", @"Documents\PowerShell\Microsoft.PowerShell_profile.ps1", "Set-Alias g git\n", "Set-Alias k kubectl\n"),
        new("windows-terminal", "windows-terminal:stable:settings", "windows-terminal", "WindowsTerminal.exe", "1.23.0", "1.17.0",
            @"AppData\Local\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json",
            """{ "profiles": { "list": [ { "guid": "{1}", "name": "Work", "startingDirectory": "C:\\Users\\alice\\work" } ] } }""", """{ "profiles": { "list": [] } }"""),
        new("wsl", "wsl:wslconfig", "wsl", null, "2.6.1", "1.2.5", ".wslconfig", "[wsl2]\nmemory=8GB\n", "[wsl2]\nmemory=4GB\n"),
        new("docker", "docker:client-config", "docker-desktop", "Docker Desktop.exe", "4.47.0", "4.20.0", @".docker\config.json", """{ "credsStore": "desktop" }""", """{ "credsStore": "wincred" }"""),
    ];

    public static AdapterCase Get(string adapterId) => All.Single(c => c.AdapterId == adapterId);

    /// <summary>A computer with every adapter's host (at the given versions) and configuration.</summary>
    public static SimulatedMachine Machine(string root, string user, HostVersions versions, bool backupContent)
    {
        string? Version(AdapterCase c) => versions switch
        {
            HostVersions.Supported => c.Supported,
            HostVersions.Outdated => c.Outdated,
            _ => null,
        };

        var windows = Get("environment");
        var b = new SimulatedMachineBuilder(user, $"{user.ToUpperInvariant()}-PC", osBuild: Version(windows) ?? string.Empty);
        var hostFolders = new List<string>();
        foreach (var c in All.Where(c => c.Executable is not null))
        {
            var folder = $@"C:\Hosts\{c.ToolId}";
            b.Executable($@"{folder}\{c.Executable}", Version(c));
            hostFolders.Add(folder);
        }

        b.UninstallEntry(RegistryHive.LocalMachine, RegistryView.Registry64, "WSL", "Windows Subsystem for Linux", Version(Get("wsl")), "Microsoft Corporation");
        b.Environment(RegistryHive.LocalMachine, "Path", string.Join(';', ["%SystemRoot%\\system32", .. hostFolders]), expand: true);
        if (backupContent)
        {
            b.Environment(RegistryHive.CurrentUser, "PROJECTS", $@"{b.Folders.UserProfile}\Projects");
        }

        foreach (var c in All.Where(c => c.Path.Length > 0))
        {
            b.File($@"{b.Folders.UserProfile}\{c.Path}", backupContent ? c.Backup : c.Existing);
        }

        return b.Build(root);
    }
}

/// <summary>A backup of every adapter's configuration made with verified host versions, and targets with each kind of version.</summary>
public sealed class AdapterSupportFixture : IAsyncLifetime
{
    private static readonly global::DevBR.Application.SecretText Password = new("adapter-tests-password");

    public TempDirectory Temp { get; } = new();

    public SevenZipArchiveService Archive { get; } = new(Loggers.For<SevenZipArchiveService>());

    public BackupOverview Overview { get; private set; } = null!;

    /// <summary>Discovery of the old computer (verified versions) and of computers with unverified versions.</summary>
    public Dictionary<HostVersions, DiscoverySnapshot> Snapshots { get; } = [];

    /// <summary>Preflight of the backup onto a target with each kind of host version.</summary>
    public Dictionary<HostVersions, RestorePreflight> Preflights { get; } = [];

    public async ValueTask InitializeAsync()
    {
        var engine = new DiscoveryEngine(DiscoveryEngine.DefaultProviders(), NullLogger<DiscoveryEngine>.Instance);
        var options = DiscoveryOptions.Default with { ScanFixedDrives = false };
        var source = AdapterCase.Machine(Temp.Combine("source"), SampleMachines.WorkstationUser, HostVersions.Supported, backupContent: true);
        var snapshot = await engine.RunAsync(source, options, null, CancellationToken.None);
        Snapshots[HostVersions.Supported] = snapshot;

        var ids = AdapterCase.All.Select(c => c.ArtifactId).Append(DiscoveryEngine.InventoryArtifactId).ToHashSet(StringComparer.Ordinal);
        var selected = snapshot.Artifacts.Where(a => ids.Contains(a.Id)).ToList();
        var plan = new BackupPlanner().Plan(new BackupPlanRequest(source, snapshot, selected, [], DefaultExclusions.All, [Temp.Path]), null, CancellationToken.None);
        var result = await new BackupRunner(Archive, NullLogger<BackupRunner>.Instance).RunAsync(plan,
            new BackupRunOptions(Temp.Combine("adapters.devbr"), Temp.Combine("scratch"), CompressionPreset.Fast, Password, false), null, CancellationToken.None);
        if (!result.Verified)
        {
            throw new InvalidOperationException($"Fixture backup failed: {result.Message}");
        }

        Overview = await new BackupReader(Archive).OpenAsync(result.OutputPath!, Password, Temp.Combine("scratch"), CancellationToken.None);

        foreach (var mode in Enum.GetValues<HostVersions>())
        {
            var target = Target(mode.ToString(), mode);
            if (mode != HostVersions.Supported)
            {
                Snapshots[mode] = await engine.RunAsync(target, options, null, CancellationToken.None);
            }

            Preflights[mode] = await PreflightAsync(target, null, CancellationToken.None);
        }
    }

    public SimulatedMachine Target(string name, HostVersions versions)
        => AdapterCase.Machine(Temp.Combine("targets", name), SampleMachines.TargetUser, versions, backupContent: false);

    public Task<RestorePreflight> PreflightAsync(SimulatedMachine target, IReadOnlyDictionary<string, ConflictDecision>? decisions, CancellationToken cancellationToken)
        => new RestorePlanner(Archive, NullLogger<RestorePlanner>.Instance).PreflightAsync(
            new RestoreRequest(Overview, Password, target, Overview.Artifacts.Select(a => a.Record.Key).ToHashSet(), [],
                decisions ?? new Dictionary<string, ConflictDecision>(), Temp.Combine("work")), null, cancellationToken);

    public ValueTask DisposeAsync()
    {
        BackupReader.Close(Overview);
        Temp.Dispose();
        return ValueTask.CompletedTask;
    }
}
