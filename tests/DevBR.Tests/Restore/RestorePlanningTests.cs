using System.Security.Cryptography;
using DevBR.Application.Machine;
using DevBR.Domain;
using DevBR.Restore;
using DevBR.Simulation;

namespace DevBR.Tests.Restore;

/// <summary>Phase 4 acceptance scenarios: preflight and restore planning against simulated target computers.</summary>
public sealed class RestorePlanningTests(RestoreFixture fixture) : IClassFixture<RestoreFixture>
{
    private static readonly RootMapping ProjectsToC = new(@"D:\Projects", @"C:\Projects", PathMappingOrigin.UserSelected);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<RestorePreflight> PreflightAsync(SimulatedMachine target, IEnumerable<RootMapping>? mappings = null, IReadOnlyDictionary<string, ConflictDecision>? decisions = null, Func<global::DevBR.Backup.ArtifactSummary, bool>? select = null)
        => fixture.Planner().PreflightAsync(fixture.Request(target, mappings, decisions, select), null, Ct);

    private static PlannedOperation Op(RestorePreflight preflight, Func<PlannedOperation, bool> predicate) => Assert.Single(preflight.Operations, o => predicate(o));

    // --- Preflight makes no changes ---------------------------------------------------------------

    [Fact]
    public async Task Preflight_makes_no_changes_to_the_target()
    {
        var target = fixture.Target("readonly");
        var before = Fingerprint(target.Root);

        await PreflightAsync(target, [ProjectsToC]);

        Assert.Equal(before, Fingerprint(target.Root));
    }

    // --- Mapping ------------------------------------------------------------------------------------

    [Fact]
    public async Task Profile_and_project_roots_map_to_correct_scoped_destinations()
    {
        var preflight = await PreflightAsync(fixture.Target("mapping"), [ProjectsToC]);

        // C:\Users\alice → C:\Users\alex, via the known folders.
        var settings = Op(preflight, o => o.Operation.Action == RestoreAction.MergeStructuredSettings && o.Operation.Target.EndsWith(@"Code\User\settings.json", StringComparison.Ordinal));
        Assert.Equal(@"C:\Users\alex\AppData\Roaming\Code\User\settings.json", settings.Operation.Target);
        Assert.Contains(preflight.Operations, o => o.Operation.Target == @"C:\Users\alex\.gitconfig");

        // D:\Projects → C:\Projects for repositories, and nothing else on D: or C: is affected.
        Assert.Equal(@"C:\Projects\webapp", Op(preflight, o => o.Operation.Action == RestoreAction.RestoreRepository && o.Title.Contains("webapp", StringComparison.Ordinal)).Operation.Target);
        Assert.Equal(@"C:\Projects\api-hotfix", Op(preflight, o => o.Title.Contains("api-hotfix", StringComparison.Ordinal)).Operation.Target);

        // The linked worktree's Git pointer is rewritten, keeping its forward-slash style.
        Assert.Contains(preflight.Rewrites, r => r.Before == "D:/Projects/api/.git/worktrees/api-hotfix" && r.After == "C:/Projects/api/.git/worktrees/api-hotfix");

        // A declared path field inside a profile is rewritten to the new user.
        Assert.Contains(preflight.Rewrites, r => r.Field.Contains("python.defaultInterpreterPath", StringComparison.Ordinal)
            && r.After == @"C:\Users\alex\AppData\Local\Programs\Python\Python312\python.exe");
    }

    [Fact]
    public void Longest_prefix_mapping_respects_path_boundaries()
    {
        var mapper = new PathMapper([
            new(@"C:\", @"C:\", PathMappingOrigin.Default),
            new(@"C:\Projects", @"D:\Projects", PathMappingOrigin.UserSelected),
            new(@"C:\Users\Alice", @"C:\Users\Bob", PathMappingOrigin.KnownFolder),
        ]);

        Assert.Equal(@"D:\Projects\app\src", mapper.Map(@"C:\Projects\app\src"));
        Assert.Equal(@"D:\Projects", mapper.Map(@"C:\Projects"));
        Assert.Equal(@"C:\ProjectsOld\app", mapper.Map(@"C:\ProjectsOld\app"));
        Assert.Equal(@"C:\Users\Bob\.gitconfig", mapper.Map(@"C:\Users\Alice\.gitconfig"));
        Assert.Equal(@"C:\Users\Alicia\x", mapper.Map(@"C:\Users\Alicia\x"));
        Assert.Equal(@"C:\Program Files\Git", mapper.Map(@"C:\Program Files\Git"));
        Assert.Null(mapper.Map(@"E:\Elsewhere"));

        var rewriter = new PathRewriter(mapper);
        Assert.Equal(RewriteOutcome.Unchanged, rewriter.Rewrite(@"%USERPROFILE%\bin").Outcome);
        Assert.Equal(RewriteOutcome.Unresolved, rewriter.Rewrite(@"E:\Elsewhere\tool.exe").Outcome);
        Assert.Equal("C:/Users/Bob/x", rewriter.Rewrite("C:/Users/Alice/x").Value);
    }

    [Fact]
    public async Task Destinations_on_missing_drives_block_with_mapping_guidance()
    {
        var preflight = await PreflightAsync(fixture.Target("nodrive"));

        var finding = Assert.Single(preflight.Findings, f => f.Severity == FindingSeverity.Blocking && f.Problem.Contains(@"D:\Projects\webapp", StringComparison.Ordinal));
        Assert.Contains(finding.NextSteps, s => s.Contains("Add a folder mapping", StringComparison.Ordinal));
        Assert.True(finding.CanRecheck);
        Assert.DoesNotContain(preflight.Operations, o => o.Operation.Action == RestoreAction.RestoreRepository && o.Title.Contains("webapp", StringComparison.Ordinal));
        Assert.Contains(preflight.Mappings, m => m.SourceAbsolutePath == @"D:\Projects\webapp" && m.Status == PathMappingStatus.Invalid);
    }

    [Fact]
    public async Task Repositories_are_restored_only_into_empty_destinations()
    {
        var target = fixture.Target("occupied", b => b.File(@"C:\Projects\webapp\existing.txt", "mine"));
        var preflight = await PreflightAsync(target, [ProjectsToC]);

        Assert.Contains(preflight.Findings, f => f.Severity == FindingSeverity.Blocking && f.Problem.Contains(@"C:\Projects\webapp already exists and is not empty", StringComparison.Ordinal));
        Assert.DoesNotContain(preflight.Operations, o => o.Operation.Action == RestoreAction.RestoreRepository && o.Title.Contains("webapp", StringComparison.Ordinal));
    }

    // --- Prerequisites -------------------------------------------------------------------------------

    [Fact]
    public async Task Missing_host_applications_produce_specific_guidance_and_manual_steps()
    {
        var preflight = await PreflightAsync(fixture.Target("hosts"), [ProjectsToC]);

        var cursor = Assert.Single(preflight.Findings, f => f.Severity == FindingSeverity.Blocking && f.Prerequisite == "Cursor");
        Assert.Contains("cursor:settings", cursor.AffectedArtifactIds);
        Assert.Contains(cursor.NextSteps, s => s.Contains("cursor.com", StringComparison.Ordinal));
        Assert.Contains(cursor.NextSteps, s => s.Contains("Recheck", StringComparison.Ordinal));

        var step = Op(preflight, o => o.Operation.Id == "host:cursor");
        Assert.Equal(RestoreAction.ManualStep, step.Operation.Action);
        Assert.All(preflight.Operations.Where(o => o.Operation.ArtifactId == "cursor:settings" && o.Operation.Action != RestoreAction.ManualStep),
            o => Assert.Contains("host:cursor", o.Operation.DependsOn));

        // VS Code is installed (and newer), so its settings are not blocked.
        Assert.DoesNotContain(preflight.Findings, f => f.Prerequisite == "Visual Studio Code" && f.Severity == FindingSeverity.Blocking);
        Assert.Contains(preflight.BlockedArtifacts, a => a == "cursor:settings");
        Assert.DoesNotContain(preflight.BlockedArtifacts, a => a == "vscode:default:settings");
    }

    [Fact]
    public async Task Older_host_versions_and_running_applications_produce_warnings()
    {
        var target = fixture.Target("older-running", b => b
            .UninstallEntry(RegistryHive.CurrentUser, RegistryView.Registry64, "{771FD6B0-FA20-440A-A002-3B3BAC16DC50}_is1", "Microsoft Visual Studio Code (User)", "1.90.0", "Microsoft Corporation",
                $@"{b.Folders.LocalAppData}\Programs\Microsoft VS Code\")
            .Executable($@"{b.Folders.LocalAppData}\Programs\Microsoft VS Code\Code.exe", "1.90.0")
            .Running("Code.exe"));
        var preflight = await PreflightAsync(target, [ProjectsToC]);

        Assert.Contains(preflight.Findings, f => f.Severity == FindingSeverity.Warning && f.Problem.Contains("1.90.0 on this computer is older than", StringComparison.Ordinal));
        Assert.Contains(preflight.Findings, f => f.Severity == FindingSeverity.Warning && f.Problem.Contains("Visual Studio Code is running", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_runtimes_for_mcp_servers_become_approved_installs_or_manual_steps()
    {
        var preflight = await PreflightAsync(fixture.Target("runtimes"), [ProjectsToC]);

        Assert.Contains(preflight.McpServers, s => s.Name == "github" && s.Kind == McpServerKind.Remote);
        Assert.Contains(preflight.McpServers, s => s.Name == "sqlite" && s.RequiredToolId == "uv");
        Assert.Contains(preflight.McpServers, s => s.Name == "playwright" && s.RequiredToolId == "node");

        var node = Op(preflight, o => o.Operation.Id == "runtime:node");
        Assert.Equal(RestoreAction.InstallDependency, node.Operation.Action);
        Assert.Equal(Reversibility.Irreversible, node.Operation.Reversibility);
        Assert.Equal("winget install --id OpenJS.NodeJS.LTS --exact --source winget", node.Recipe!.Preview);
        Assert.Equal(PrivilegeRequirement.Elevated, node.Operation.Privilege);
        Assert.Contains(preflight.Operations, o => o.Operation.Id == "runtime:uv");

        // An unrecognized command is restored as written and left to the user, never guessed.
        Assert.Contains(preflight.Findings, f => f.Problem.Contains("'internal'", StringComparison.Ordinal) && f.Severity == FindingSeverity.Information);
        Assert.DoesNotContain(preflight.Operations, o => o.Recipe?.Preview.Contains("acme", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Package_installs_respect_policy()
    {
        var target = fixture.Target("policy", b => b.RegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Policies\Microsoft\Windows\AppInstaller", "EnableAppInstaller", RegistryValueKind.DWord, 0));
        var preflight = await PreflightAsync(target, [ProjectsToC]);

        Assert.DoesNotContain(preflight.Operations, o => o.Recipe?.Kind == RecipeKind.WinGet);
        Assert.Contains(preflight.Findings, f => f.Problem.Contains("need Node.js", StringComparison.Ordinal) && f.NextSteps.Any(s => s.Contains("Install", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Missing_extensions_are_planned_with_validated_recipes()
    {
        var preflight = await PreflightAsync(fixture.Target("extensions"), [ProjectsToC]);

        var python = Op(preflight, o => o.Recipe?.PackageId == "ms-python.python");
        Assert.Equal("code --install-extension ms-python.python@2025.14.0", python.Recipe!.Preview);
        Assert.Equal(Reversibility.Irreversible, python.Operation.Reversibility);

        Assert.Null(PackageRecipes.Extension("vscode", "evil.ext; Remove-Item C:\\", null));
        Assert.Null(PackageRecipes.Extension("vscode", "good.ext", "1.0.0 && calc"));
        Assert.Null(PackageRecipes.Extension("notepad", "good.ext", null));
    }

    // --- Existing values are preserved --------------------------------------------------------------

    [Fact]
    public async Task Existing_values_are_preserved_by_default_and_replaced_only_on_request()
    {
        var preflight = await PreflightAsync(fixture.Target("merge"), [ProjectsToC]);
        var settings = Op(preflight, o => o.Operation.Target.EndsWith(@"Roaming\Code\User\settings.json", StringComparison.Ordinal));

        Assert.Equal(ConflictDecision.Merge, settings.Operation.ConflictDecision);
        Assert.Contains(settings.Merge!.Changes, c => c.Path == "editor.fontSize" && c.Kind == MergeChangeKind.ConflictKeptTarget && c.TargetValue == "16");
        Assert.Contains(settings.Merge.Changes, c => c.Path == "files.autoSave" && c.Kind == MergeChangeKind.Added);
        Assert.Contains(ConflictDecision.UseBackup, settings.AllowedDecisions);

        var overridden = await PreflightAsync(fixture.Target("merge-backup"), [ProjectsToC],
            new Dictionary<string, ConflictDecision> { [settings.Operation.Id] = ConflictDecision.UseBackup });
        var replaced = Op(overridden, o => o.Operation.Id == settings.Operation.Id);
        Assert.Contains(replaced.Merge!.Changes, c => c.Path == "editor.fontSize" && c.Kind == MergeChangeKind.ConflictUsedBackup);
    }

    [Fact]
    public async Task Whole_files_without_structured_support_keep_the_target_by_default()
    {
        var target = fixture.Target("wholefile", b => b.File($@"{b.Folders.UserProfile}\.gitconfig", "[user]\n\tname = Alex\n"));
        var preflight = await PreflightAsync(target, [ProjectsToC]);

        var gitconfig = Op(preflight, o => o.Operation.Target == @"C:\Users\alex\.gitconfig");
        Assert.Equal(RestoreAction.Skip, gitconfig.Operation.Action);
        Assert.Equal(ConflictDecision.KeepExisting, gitconfig.Operation.ConflictDecision);
        Assert.Equal([ConflictDecision.KeepExisting, ConflictDecision.UseBackup, ConflictDecision.RestoreAlongside], gitconfig.AllowedDecisions);

        var alongside = await PreflightAsync(target, [ProjectsToC], new Dictionary<string, ConflictDecision> { [gitconfig.Operation.Id] = ConflictDecision.RestoreAlongside });
        Assert.Equal(@"C:\Users\alex\.gitconfig.restored", Op(alongside, o => o.Operation.Id == gitconfig.Operation.Id).Operation.Target);
        Assert.Equal(@"C:\Users\alex\settings.restored.json", RestorePlanner.AlongsidePath(@"C:\Users\alex\settings.json"));
    }

    [Fact]
    public async Task Structured_merges_are_scoped_to_the_declared_keys()
    {
        var target = fixture.Target("claude-json", b => b.File($@"{b.Folders.UserProfile}\.claude.json",
            """{ "numStartups": 3, "mcpServers": { "filesystem": { "command": "npx", "args": ["other"] } } }"""));
        var preflight = await PreflightAsync(target, [ProjectsToC]);

        var claude = Op(preflight, o => o.Operation.Target == @"C:\Users\alex\.claude.json");
        Assert.All(claude.Merge?.Changes ?? [], c => Assert.StartsWith("mcpServers", c.Path, StringComparison.Ordinal));
        Assert.Contains(claude.Merge?.Changes ?? [], c => c.Path == "mcpServers.filesystem" && c.Kind == MergeChangeKind.ConflictKeptTarget);
    }

    // --- Environment --------------------------------------------------------------------------------

    [Fact]
    public async Task Environment_changes_are_scoped_appended_and_held_back_when_folders_are_missing()
    {
        var preflight = await PreflightAsync(fixture.Target("environment"), [ProjectsToC]);

        // Custom user variable with a mapped path value.
        var projects = Op(preflight, o => o.Operation.Target == "ENV:User:PROJECTS");
        Assert.Equal(RestoreAction.SetEnvironmentVariable, projects.Operation.Action);
        Assert.Contains(preflight.Rewrites, r => r.Field == "PROJECTS (User)" && r.After == @"C:\Projects");

        // Machine variable needs elevation; system-defined variables are never proposed.
        Assert.Equal(PrivilegeRequirement.Elevated, Op(preflight, o => o.Operation.Target == "ENV:Machine:DOTNET_CLI_TELEMETRY_OPTOUT").Operation.Privilege);
        Assert.DoesNotContain(preflight.Operations, o => o.Operation.Target is "ENV:Machine:OS" or "ENV:Machine:ComSpec" or "ENV:Machine:PROCESSOR_ARCHITECTURE");

        // PATH: entries already present add nothing; missing folders are held back, expressions preserved.
        var paths = preflight.Operations.Where(o => o.Operation.Action == RestoreAction.AppendPathEntry).ToList();
        Assert.DoesNotContain(paths, o => o.Operation.ExpectedTargetState!.Contains("Microsoft VS Code\\bin", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, o => o.Operation.ExpectedTargetState == @"C:\Program Files\Git\cmd");
        var localBin = Op(preflight, o => o.Operation.ExpectedTargetState == @"%USERPROFILE%\.local\bin");
        Assert.False(localBin.Enabled);
        Assert.Contains(preflight.Findings, f => f.Problem.Contains("PATH entries point to folders that do not exist", StringComparison.Ordinal));

        // CODEX_HOME points at D:\ai\codex, which has no destination: held back, never set to a dead path.
        Assert.False(Op(preflight, o => o.Operation.Target == "ENV:User:CODEX_HOME").Enabled);
        Assert.DoesNotContain(preflight.Effects, o => o.Operation.Target == "ENV:User:CODEX_HOME");

        // Secrets were not captured, so nothing secret is proposed.
        Assert.DoesNotContain(preflight.Operations, o => o.Operation.Target.Contains("GITHUB_TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Planning_twice_against_an_unchanged_target_proposes_the_same_effects()
    {
        var target = fixture.Target("repeat");
        var first = await PreflightAsync(target, [ProjectsToC]);
        var second = await PreflightAsync(target, [ProjectsToC]);
        Assert.Equal(first.Plan.ApprovalHash, second.Plan.ApprovalHash);
        Assert.Equal(PlanApproval.EffectKeys(first), PlanApproval.EffectKeys(second));
    }

    // --- Approval -----------------------------------------------------------------------------------

    [Fact]
    public async Task Plan_changes_invalidate_approval_only_when_they_add_effects()
    {
        var target = fixture.Target("approval");
        var reviewed = await PreflightAsync(target, [ProjectsToC]);
        var approval = PlanApproval.Approve(reviewed);

        Assert.True(approval.Covers(await PreflightAsync(target, [ProjectsToC])));

        // Fewer items: no new effect, the approval still holds.
        Assert.True(approval.Covers(await PreflightAsync(target, [ProjectsToC], select: a => a.Record.Artifact.Kind != ArtifactKind.Repository)));

        // Choosing the backup's value replaces something new: approve again.
        var settingsId = reviewed.Operations.Single(o => o.Operation.Target.EndsWith(@"Roaming\Code\User\settings.json", StringComparison.Ordinal)).Operation.Id;
        Assert.False(approval.Covers(await PreflightAsync(target, [ProjectsToC], new Dictionary<string, ConflictDecision> { [settingsId] = ConflictDecision.UseBackup })));

        // A different destination is a different effect, too.
        Assert.False(approval.Covers(await PreflightAsync(target, [new RootMapping(@"D:\Projects", @"C:\Work", PathMappingOrigin.UserSelected)])));
    }

    // --- Capacity, privilege, credentials -----------------------------------------------------------

    [Fact]
    public async Task Insufficient_space_privilege_and_credentials_produce_specific_findings()
    {
        var target = fixture.Target("tiny");
        var definition = target.Definition;
        definition.Drives[0] = definition.Drives[0] with { FreeBytes = 1024 };
        await File.WriteAllTextAsync(Path.Combine(target.Root, "machine.json"), System.Text.Json.JsonSerializer.Serialize(definition, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }), Ct);
        var preflight = await PreflightAsync(SimulatedMachine.Load(target.Root), [ProjectsToC]);

        Assert.Contains(preflight.Findings, f => f.Severity == FindingSeverity.Blocking && f.Problem.StartsWith(@"Drive C:\ has", StringComparison.Ordinal));
        Assert.Contains(preflight.Findings, f => f.Prerequisite == "Administrator approval" && f.Problem.Contains("need administrator approval", StringComparison.Ordinal));
        Assert.Contains(preflight.Findings, f => f.Prerequisite == "Sign-in" && f.NextSteps.Contains("GitHub CLI: run 'gh auth login'."));
    }

    [Fact]
    public async Task Reinstall_guidance_lists_software_missing_from_the_target()
    {
        var preflight = await PreflightAsync(fixture.Target("reinstall"), [ProjectsToC]);

        Assert.Contains(preflight.Reinstall, r => r.Name == "Node.js" && r.SourceVersion == "22.11.0");
        Assert.Contains(preflight.Reinstall, r => r.Name == "Docker Desktop");
        Assert.DoesNotContain(preflight.Reinstall, r => r.Name is "Git" or "Visual Studio Code" or "Windows Terminal");
    }

    [Fact]
    public void Version_comparison_handles_build_suffixes()
    {
        Assert.True(RestorePlanner.CompareVersions("1.106.0", "1.105.1") > 0);
        Assert.True(RestorePlanner.CompareVersions("1.90.0", "1.105.1") < 0);
        Assert.Equal(0, RestorePlanner.CompareVersions("7.5.3.0", "7.5.3"));
        Assert.True(RestorePlanner.CompareVersions("2.55.0.windows.1", "2.54.9") > 0);
    }

    /// <summary>Hash of every byte and name in a simulated machine folder.</summary>
    private static string Fingerprint(string root)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(path));
            if (File.Exists(path))
            {
                sha.AppendData(File.ReadAllBytes(path));
                sha.AppendData(BitConverter.GetBytes(File.GetLastWriteTimeUtc(path).Ticks));
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
