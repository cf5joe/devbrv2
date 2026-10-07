using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DevBR.Discovery;
using DevBR.Domain;
using DevBR.Simulation;

namespace DevBR.Tests.Discovery;

/// <summary>Phase 2 acceptance scenarios, run against the simulated developer workstation.</summary>
public sealed class DiscoveryTests(WorkstationFixture fixture) : IClassFixture<WorkstationFixture>
{
    private DiscoverySnapshot Snapshot => fixture.Snapshot;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

/// <summary>Installations of a tool (not its configuration, extensions, repositories or WSL distributions).</summary>
    private IEnumerable<InventoryItem> Tool(string toolId) => Snapshot.Items.Where(i => i.ToolId == toolId
        && i.Category is InventoryCategory.Application or InventoryCategory.DeveloperTool or InventoryCategory.Runtime or InventoryCategory.PackageManager
        && !i.Name.StartsWith("WSL distribution", StringComparison.Ordinal));

    private InventoryItem Repository(string path) => Assert.Single(Snapshot.Items, i => i.Category == InventoryCategory.Repository && i.Locations[0].Equals(path, StringComparison.OrdinalIgnoreCase));

    private MigrationArtifact Artifact(string id) => Assert.Single(Snapshot.Artifacts, a => a.Id == id);

    private IEnumerable<ExcludedScope> Exclusions => Snapshot.Coverage.SelectMany(c => c.Exclusions);

    [Fact]
    public void Every_provider_completed_without_errors()
        => Assert.Empty(Snapshot.Coverage.SelectMany(c => c.Errors));

    // --- Installed software -------------------------------------------------------------------------

    [Fact]
    public void Reads_both_registry_views_and_both_hives()
    {
        var sevenZip = Assert.Single(Snapshot.Items, i => i.Name == "7-Zip 24.08");
        Assert.Equal(InstallScope.Machine, sevenZip.Scope);
        Assert.Contains(sevenZip.Evidence, e => e.Source.Contains("HKLM32", StringComparison.Ordinal));

        var vscode = Assert.Single(Tool("vscode"), i => i.Scope == InstallScope.User);
        Assert.Contains(vscode.Evidence, e => e.Source.Contains("HKCU", StringComparison.Ordinal));
        Assert.Equal(InstallScope.Machine, Assert.Single(Tool("git")).Scope);
    }

    [Fact]
    public void Hidden_components_and_updates_are_skipped_and_reported()
    {
        Assert.DoesNotContain(Snapshot.Items, i => i.Name.Contains("Minimum Runtime", StringComparison.Ordinal) || i.Name.Contains("KB5040000", StringComparison.Ordinal));
        Assert.Contains(Exclusions, e => e.Reason == "Hidden system component");
        Assert.Contains(Exclusions, e => e.Reason == "Update or hotfix for another product");
    }

    [Fact]
    public void Observations_of_one_installation_are_merged()
    {
        var git = Assert.Single(Tool("git"));
        Assert.Equal(DetectionStatus.ConfirmedInstalled, git.Status);
        Assert.Contains(git.Evidence, e => e.Source.StartsWith("registry:", StringComparison.Ordinal));
        Assert.Contains(git.Evidence, e => e.Source.StartsWith("PATH", StringComparison.Ordinal));
        Assert.Contains(git.Evidence, e => e.Source == "start-menu");
        Assert.Contains(git.Evidence, e => e.Source == "filesystem");
    }

    [Fact]
    public void Distinct_installations_and_versions_are_kept_apart()
    {
        var pythons = Tool("python").ToList();
        Assert.Equal(2, pythons.Count);
        Assert.Contains(pythons, p => p.Scope == InstallScope.User && p.Locations[0].Contains("Python312", StringComparison.Ordinal));
        Assert.Contains(pythons, p => p.Scope == InstallScope.Machine && p.Locations[0].Contains("Python311", StringComparison.Ordinal));

        var portable = Assert.Single(Tool("vscode"), i => i.Scope == InstallScope.Portable);
        Assert.Equal(@"D:\Tools\VSCode-portable", portable.Locations[0]);
        Assert.Equal("true", portable.Properties?["portableMode"]);
        Assert.Equal("1.104.0", portable.Version);
    }

    [Fact]
    public void Store_packages_are_inventoried_through_the_package_source()
    {
        Assert.Equal(InstallScope.Store, Assert.Single(Tool("windows-terminal")).Scope);
        Assert.Equal(InstallScope.Store, Assert.Single(Tool("claude-desktop")).Scope);
        Assert.Equal(InstallScope.Store, Assert.Single(Tool("wsl")).Scope);
    }

    [Fact]
    public void Same_executable_name_is_attributed_by_location()
    {
        var claudeCode = Assert.Single(Tool("claude-code"));
        Assert.Contains(claudeCode.Locations, l => l.EndsWith(@".local\bin", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("2.1.0", claudeCode.Version);
    }

    [Fact]
    public void Package_manager_metadata_is_read_without_running_anything()
    {
        var packages = Snapshot.Items.Where(i => i.Properties?.ContainsKey("package") == true)
            .ToDictionary(i => $"{i.Properties!["packageManager"]}:{i.Properties["package"]}", i => i.Version);

        Assert.Equal("0.50.0", packages["npm:@openai/codex"]);
        Assert.Equal("5.9.3", packages["npm:typescript"]);
        Assert.Equal("1.7.1", packages["scoop:jq"]);
        Assert.Equal("14.1.1", packages["chocolatey:ripgrep"]);
        Assert.True(packages.ContainsKey("winget:sharkdp.fd"));
        Assert.Equal("9.0.0", packages["dotnet-tool:dotnet-ef"]);
        Assert.Equal("24.10.0", packages["pipx:black"]);
        Assert.True(packages.ContainsKey("uv:ruff"));
        Assert.Equal("2.32.3", packages["pip:requests"]);
        Assert.Contains(Snapshot.Items, i => i.ToolId == "codex" && i.Category == InventoryCategory.DeveloperTool);
    }

    // --- Environment and secrets --------------------------------------------------------------------

    [Fact]
    public void Environment_scopes_types_and_secrets_are_preserved_and_protected()
    {
        var userPath = Assert.Single(Snapshot.Items, i => i.Category == InventoryCategory.EnvironmentVariable && i.Name == "Path" && i.Scope == InstallScope.User);
        Assert.Equal("REG_EXPAND_SZ", userPath.Properties!["registryType"]);
        Assert.Equal("4", userPath.Properties["entries"]);

        var token = Assert.Single(Snapshot.Items, i => i.Name == "GITHUB_TOKEN");
        Assert.Equal("true", token.Properties!["sensitive"]);

        var secret = Artifact("environment:secret:user:GITHUB_TOKEN");
        Assert.Equal(Sensitivity.Credential, secret.Sensitivity);
        Assert.Equal(BackupEligibility.ExcludedByDefault, secret.Eligibility);
    }

    [Fact]
    public void No_secret_value_is_stored_in_the_snapshot()
    {
        var json = JsonSerializer.Serialize(Snapshot);
        Assert.DoesNotContain("ghp_SIMULATED", json, StringComparison.Ordinal);
        Assert.Contains("github.com/alice/api.git", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_path_entries_are_reported()
        => Assert.Contains(Exclusions, e => e.Path == @"C:\Missing\Tool\bin" && e.Reason.Contains("does not exist", StringComparison.Ordinal));

    // --- Filesystem pass ----------------------------------------------------------------------------

    [Fact]
    public void Repository_kinds_and_states_are_recognized()
    {
        var webapp = Repository(@"D:\Projects\webapp");
        Assert.Equal("standard", webapp.Properties!["kind"]);
        Assert.Equal("main", webapp.Properties["head"]);
        Assert.Equal("1", webapp.Properties["submodules"]);

        var api = Repository(@"D:\Projects\api");
        Assert.Equal("develop", api.Properties!["head"]);
        Assert.Equal("true", api.Properties["lfs"]);
        Assert.Equal("true", api.Properties["stash"]);
        Assert.Equal("1", api.Properties["linkedWorktrees"]);
        Assert.Equal("true", api.Properties["credentialsInRemoteUrl"]);
        Assert.Contains("alice:***@github.com", api.Properties["remotes"], StringComparison.Ordinal);

        Assert.Equal("linked worktree", Repository(@"D:\Projects\api-hotfix").Properties!["kind"]);
        Assert.Equal("submodule", Repository(@"D:\Projects\webapp\libs\shared").Properties!["kind"]);
        Assert.Equal("bare", Repository(@"D:\Mirrors\monorepo.git").Properties!["kind"]);
        Assert.Contains(@"D:\Mirrors\monorepo.git\objects", Repository(@"D:\Projects\monorepo-fork").Properties!["objectAlternates"], StringComparison.Ordinal);
        Assert.Equal("true", Repository(@"C:\Users\alice\source\repos\scratch").Properties!["locked"]);
    }

    [Fact]
    public void Repository_artifacts_carry_dependencies_and_are_not_selected()
    {
        var api = Artifact($"repository:{Repository(@"D:\Projects\api").Id}");
        var hotfix = Artifact($"repository:{Repository(@"D:\Projects\api-hotfix").Id}");

        Assert.Contains(api.Id, hotfix.Dependencies);
        Assert.False(api.SelectedByDefault);
        Assert.Equal(Sensitivity.ContainsRecognizedSecrets, api.Sensitivity);
        Assert.Equal(LogicalRootKind.RepositoryRoot, api.Roots[0].Root);
    }

    [Fact]
    public void Project_configuration_is_linked_to_its_repository()
    {
        var configs = Snapshot.Items.Where(i => i.Category == InventoryCategory.AiConfiguration).ToList();
        Assert.Contains(configs, c => c.ToolId == "claude-code" && c.Properties?["repository"] == @"D:\Projects\webapp");
        Assert.Contains(configs, c => c.ToolId == "copilot-cli" && c.Properties?["repository"] == @"D:\Projects\webapp");
        Assert.Contains(configs, c => c.ToolId == "vscode" && c.Properties?["repository"] == @"D:\Projects\webapp");
        Assert.True(int.Parse(Repository(@"D:\Projects\webapp").Properties!["projectConfigs"], System.Globalization.CultureInfo.InvariantCulture) >= 4);

        // AGENTS.md in the CODEX_HOME override is user-level Codex configuration, not a project.
        Assert.DoesNotContain(configs, c => c.Locations[0].StartsWith(@"D:\ai\codex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Junctions_are_reported_and_not_followed()
    {
        Assert.Contains(Exclusions, e => e.Path == @"C:\Users\alice\Links\projects" && e.Reason.Contains("Junction", StringComparison.Ordinal));
        Assert.Single(Snapshot.Items, i => i.Category == InventoryCategory.Repository && i.Name == "webapp");
    }

    [Fact]
    public void Other_users_profiles_are_excluded_visibly()
    {
        Assert.DoesNotContain(Snapshot.Items, i => i.Locations.Any(l => l.StartsWith(@"C:\Users\bob", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(Exclusions, e => e.Path == @"C:\Users\bob" && e.Reason == "Another user's profile (not entered)");
    }

    [Fact]
    public async Task Other_users_profiles_can_be_included_explicitly()
    {
        var snapshot = await WorkstationFixture.Engine().RunAsync(fixture.Machine, DiscoveryOptions.Default with { IncludeOtherUserProfiles = true }, null, Ct);
        Assert.Contains(snapshot.Items, i => i.Category == InventoryCategory.Repository && i.Name == "private-repo");
    }

    [Fact]
    public void Inaccessible_and_protected_locations_are_reported()
    {
        Assert.Contains(Snapshot.Coverage.SelectMany(c => c.Inaccessible), i => i.Path == @"C:\ProgramData\Restricted");
        Assert.Contains(Exclusions, e => e.Reason == "Recycle bin");
        Assert.Contains(Exclusions, e => e.Reason == "Protected operating-system data");
        Assert.Contains(Exclusions, e => e.Path == @"C:\Windows" && e.Reason == "Windows system folder");
        Assert.Contains(Exclusions, e => e.Reason == "Dependency folder (regenerable)");
        Assert.DoesNotContain(Snapshot.Items, i => i.Locations.Any(l => l.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Custom_roots_are_scanned_only_when_added()
    {
        Assert.Single(Snapshot.Items, i => i.Category == InventoryCategory.Repository && i.Name == "old-tool");

        var without = await WorkstationFixture.Engine().RunAsync(fixture.Machine, DiscoveryOptions.Default, null, Ct);
        Assert.DoesNotContain(without.Items, i => i.Name == "old-tool");
    }

    [Fact]
    public void Coverage_reports_every_scanned_drive()
    {
        var drives = Snapshot.Coverage.Where(c => c.Source == "Local drives").Select(c => c.Volume).ToList();
        Assert.Contains(@"C:\", drives);
        Assert.Contains(@"D:\", drives);
        Assert.Contains(@"E:\Archive", drives);
        Assert.All(Snapshot.Coverage.Where(c => c.Source == "Local drives"), c => Assert.True(c.ScannedDirectories > 0));
    }

    // --- Adapters -----------------------------------------------------------------------------------

    [Fact]
    public void Environment_overrides_take_precedence_over_default_locations()
    {
        var codex = Artifact("codex:config");
        Assert.Equal(LogicalRootKind.CustomRoot, codex.Roots[0].Root);
        Assert.Equal(@"D:\ai\codex", codex.Roots[0].RootKey);
        Assert.Contains("2 MCP servers", codex.Description, StringComparison.Ordinal);
        Assert.Equal(BackupEligibility.ExcludedByDefault, Artifact("codex:auth").Eligibility);
    }

    [Fact]
    public void Vs_code_profiles_keep_their_own_scope()
    {
        Assert.Equal(LogicalRootKind.RoamingAppData, Artifact("vscode:default:settings").Roots[0].Root);
        var profile = Artifact("vscode:profile--5a2f1c:settings");
        Assert.StartsWith("Profile \"Data Science\"", profile.DisplayName, StringComparison.Ordinal);
        Assert.Equal(@"Code\User\profiles\-5a2f1c\settings.json", profile.Roots[0].RelativePath);
        Assert.Equal(2, Snapshot.Items.Count(i => i.Category == InventoryCategory.Extension && i.ToolId == "vscode"));
    }

    [Fact]
    public void Ai_tool_adapters_find_settings_and_separate_credentials()
    {
        Assert.Contains("1 hook", Artifact("claude-code:settings").Description, StringComparison.Ordinal);
        Assert.Equal(Sensitivity.MayContainSecrets, Artifact("claude-code:user-mcp").Sensitivity);
        Assert.Equal(Sensitivity.Credential, Artifact("claude-code:credentials").Sensitivity);
        Assert.Contains(Snapshot.Items, i => i.Name.Contains("managed settings", StringComparison.Ordinal));

        Artifact("copilot:mcp");
        Artifact("copilot:skills");
        Artifact("cursor:mcp");
        Artifact("claude-desktop:config");
        Assert.Equal(BackupEligibility.InventoryOnly, Artifact("claude-desktop:extensions").Eligibility);
        Assert.Equal(BackupEligibility.Blocked, Artifact("gemini-cli:oauth").Eligibility);
        Artifact("gemini-cli:commands");
    }

    [Fact]
    public void Shell_and_platform_adapters_describe_their_artifacts()
    {
        Assert.Equal(2, Artifact("git:config").Roots.Count); // .gitconfig + included .gitconfig-work
        Assert.Equal(Sensitivity.Credential, Artifact("git:credentials-store").Sensitivity);
        Artifact("gh:config");

        var profiles = Artifact("powershell:ps7:profiles");
        Assert.Contains(profiles.Roots, r => r.RelativePath.EndsWith(@"Scripts\aliases.ps1", StringComparison.Ordinal));
        Assert.Contains("1 locally authored module", Artifact("powershell:ps7:modules").Description, StringComparison.Ordinal);
        Assert.Contains(Snapshot.Items, i => i.Name == "posh-git" && i.Category == InventoryCategory.Package);

        Assert.Contains("2 profiles", Artifact("windows-terminal:stable:settings").Description, StringComparison.Ordinal);
        Artifact("windows-terminal:stable:assets");
        Assert.Contains(Snapshot.Items, i => i.Name == "WSL distribution: Ubuntu-24.04");
        Assert.Equal(BackupEligibility.InventoryOnly, Artifact("wsl:distributions").Eligibility);
        Assert.Equal(Sensitivity.ContainsRecognizedSecrets, Artifact("docker:client-config").Sensitivity);
    }

    // --- Selection defaults -------------------------------------------------------------------------

    [Fact]
    public void Baseline_selection_is_inventory_and_machine_environment_only()
    {
        var selected = Snapshot.Artifacts.Where(a => a.SelectedByDefault).Select(a => a.Id).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["environment:machine", DiscoveryEngine.InventoryArtifactId], selected);
        Assert.DoesNotContain(Snapshot.Artifacts, a => a.Kind == ArtifactKind.Repository && a.SelectedByDefault);
    }

    // --- Safety gates -------------------------------------------------------------------------------

    [Fact]
    public async Task Discovery_is_cancellable_responsive_and_keeps_partial_results()
    {
        // A provider that never finishes on its own stands in for a very large drive.
        var engine = new DiscoveryEngine([.. DiscoveryEngine.DefaultProviders(), new EndlessProvider()], Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscoveryEngine>.Instance);
        using var cts = new CancellationTokenSource();
        var reports = 0;
        var progress = new SynchronousProgress<DiscoveryProgress>(p =>
        {
            if (Interlocked.Increment(ref reports) == 2)
            {
                cts.Cancel();
            }
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = await engine.RunAsync(fixture.Machine, DiscoveryOptions.Default, progress, cts.Token);

        Assert.True(snapshot.Cancelled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {stopwatch.Elapsed}.");
        Assert.True(reports >= 2, "Progress must keep flowing while discovery runs.");
        Assert.NotEmpty(snapshot.Items);
        Assert.Contains(snapshot.Coverage, c => c.Errors.Any(e => e.Contains("cancelled", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancelling_before_start_returns_immediately()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var snapshot = await WorkstationFixture.Engine().RunAsync(fixture.Machine, DiscoveryOptions.Default, null, cts.Token);
        Assert.True(snapshot.Cancelled);
    }

    private sealed class EndlessProvider : DevBR.Application.Discovery.IDiscoveryProvider
    {
        public string Id => "endless";

        public string DisplayName => "Endless";

        public async Task<DevBR.Application.Discovery.DiscoveryResult> DiscoverAsync(DevBR.Application.Discovery.DiscoveryContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return DevBR.Application.Discovery.DiscoveryResult.Empty;
        }
    }

    [Fact]
    public void Discovery_code_cannot_start_processes()
    {
        // Discovery must never launch what it finds: the assembly may not reference Process at all.
        using var stream = File.OpenRead(typeof(DiscoveryEngine).Assembly.Location);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var references = reader.TypeReferences.Select(h => reader.GetTypeReference(h))
            .Select(t => $"{reader.GetString(t.Namespace)}.{reader.GetString(t.Name)}").ToList();

        Assert.DoesNotContain("System.Diagnostics.Process", references);
        Assert.DoesNotContain("System.Diagnostics.ProcessStartInfo", references);
    }

    [Fact]
    public void Simulated_machine_round_trips_through_its_fixture_folder()
    {
        var reloaded = SimulatedMachine.Load(fixture.Machine.Root);
        Assert.Equal("ALICE-DEV", reloaded.Info.ComputerName);
        Assert.True(reloaded.IsSimulated);
        Assert.Equal(@"C:\Users\alice", reloaded.Folders.UserProfile);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
