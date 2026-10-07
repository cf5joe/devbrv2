using System.Text;
using DevBR.Application.Machine;
using DevBR.Application.Restore;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;
using DevBR.Restore;
using DevBR.Restore.Execution;
using DevBR.Simulation;

namespace DevBR.Tests.Restore;

/// <summary>Phase 5 acceptance scenarios: executing, journaling, recovering and rolling back restores on simulated computers.</summary>
public sealed class RestoreExecutionTests(RestoreFixture fixture) : IClassFixture<RestoreFixture>
{
    private static readonly RootMapping ProjectsToC = new(@"D:\Projects", @"C:\Projects", PathMappingOrigin.UserSelected);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Everything one restore needs: a target computer, its writer, a journal and a rollback store.</summary>
    private sealed class Harness
    {
        private readonly RestoreFixture _fixture;

        public Harness(RestoreFixture fixture, string name, Action<SimulatedMachineBuilder>? customize = null)
        {
            _fixture = fixture;
            Target = fixture.Target(name, customize);
            Writer = new SimulatedMachineWriter(Target);
            var paths = new AppPaths(fixture.Temp.Combine("state", name));
            paths.EnsureCreated();
            var database = new StateDatabase(paths, Loggers.For<StateDatabase>());
            database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            Journal = new SqliteRestoreJournal(database);
            Store = new ProtectedRollbackStore(paths.RollbackDirectory);
            Installer = new SimulatedPackageInstaller(Target, Writer);
            Reports = fixture.Temp.Combine("reports", name);
        }

        public SimulatedMachine Target { get; }

        public SimulatedMachineWriter Writer { get; }

        public SqliteRestoreJournal Journal { get; }

        public ProtectedRollbackStore Store { get; }

        public SimulatedPackageInstaller Installer { get; }

        public string Reports { get; }

        public SimulatedElevationProvider Elevation(bool approve = true)
            => new(Target, Writer, approve, id => RestoreJobSummary.ApprovedEffects(Journal, id));

        public RestoreRequest Request(Func<global::DevBR.Backup.ArtifactSummary, bool>? select = null, IReadOnlyDictionary<string, ConflictDecision>? decisions = null)
            => _fixture.Request(Target, [ProjectsToC], decisions, select);

        public async Task<(RestorePreflight Preflight, RestoreRun Run)> RestoreAsync(RestoreRequest? request = null, bool approveElevation = true, IRestoreJournal? journal = null)
        {
            request ??= Request();
            var preflight = await _fixture.Planner().PreflightAsync(request, null, Ct);
            var run = await Executor(journal).ExecuteAsync(
                new RestoreExecutionRequest(request, PlanApproval.Approve(preflight), Writer, Elevation(approveElevation), Installer, Reports), null, Ct);
            return (preflight, run);
        }

        public RestoreExecutor Executor(IRestoreJournal? journal = null)
            => new(_fixture.Planner(), _fixture.Archive, journal ?? Journal, Store, Loggers.For<RestoreExecutor>());

        public RestoreRollbackService Rollback() => new(Journal, Store, Loggers.For<RestoreRollbackService>());

        public string Read(string path) => File.ReadAllText(Target.MapPath(path));

        public bool Exists(string path) => File.Exists(Target.MapPath(path)) || Directory.Exists(Target.MapPath(path));

        public string? Variable(RegistryHive hive, string name) => Target.ReadEnvironment(hive).GetValueOrDefault(name)?.AsString();
    }

    /// <summary>WinGet disabled by policy, so no runtimes are installed (installs are the one thing rollback cannot undo).</summary>
    private static void NoWinGet(SimulatedMachineBuilder b)
        => b.RegistryValue(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Policies\Microsoft\Windows\AppInstaller", "EnableAppInstaller", RegistryValueKind.DWord, 0);

    private const string VsCodeSettings =@"C:\Users\alex\AppData\Roaming\Code\User\settings.json";

    // --- Execution --------------------------------------------------------------------------------

    [Fact]
    public async Task An_approved_restore_applies_the_plan_and_reports_verification()
    {
        var h = new Harness(fixture, "full");
        var (_, run) = await h.RestoreAsync();

        Assert.NotEqual(RestoreRunOutcome.ApprovalOutdated, run.Outcome);
        Assert.DoesNotContain(run.Operations, o => o.Status == RestoreStatus.Failed);

        // Structured merge kept this computer's comment and value, and added what was missing.
        var settings = h.Read(VsCodeSettings);
        Assert.Contains("// My new laptop", settings, StringComparison.Ordinal);
        Assert.Contains("\"editor.fontSize\": 16", settings, StringComparison.Ordinal);
        Assert.NotNull(JsonMerger.Parse(Encoding.UTF8.GetBytes(settings)));
        Assert.Contains(run.Operations, o => o.Action == RestoreAction.MergeStructuredSettings && o.Status == RestoreStatus.Applied && o.Verification == VerificationLevel.ConfigurationApplied);

        // Files new to this computer were created, with declared paths rewritten for the new user.
        Assert.True(h.Exists(@"C:\Users\alex\.gitconfig"));
        Assert.DoesNotContain(@"C:\Users\alice", string.Concat(run.Operations.Where(o => o.Status == RestoreStatus.Applied).Select(o => o.Title)), StringComparison.OrdinalIgnoreCase);

        // Repositories: complete, verified, and the linked worktree points at the new location.
        Assert.True(h.Exists(@"C:\Projects\webapp\.git\HEAD"));
        Assert.True(h.Exists(@"C:\Projects\webapp\src\untracked-notes.txt") || h.Exists(@"C:\Projects\webapp\.git\index"));
        Assert.Contains(run.Operations, o => o.Action == RestoreAction.RestoreRepository && o.Verification == VerificationLevel.FunctionallyVerified);
        var worktree = Directory.GetFiles(h.Target.MapPath(@"C:\Projects\api\.git\worktrees"), "gitdir", SearchOption.AllDirectories);
        Assert.All(worktree, f => Assert.StartsWith("C:/Projects/", File.ReadAllText(f), StringComparison.Ordinal));

        // Environment: user values set, PATH appended (never replaced), machine values set through elevation.
        Assert.Equal("code --wait", h.Variable(RegistryHive.CurrentUser, "EDITOR"));
        Assert.Equal(@"C:\Projects", h.Variable(RegistryHive.CurrentUser, "PROJECTS"));
        Assert.StartsWith(@"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin", h.Variable(RegistryHive.CurrentUser, "Path"), StringComparison.Ordinal);
        Assert.Equal("1", h.Variable(RegistryHive.LocalMachine, "DOTNET_CLI_TELEMETRY_OPTOUT"));
        Assert.True(h.Writer.Broadcasts >= 1);

        // Installations ran through the approved recipes only, and were detected afterwards.
        Assert.NotEmpty(h.Installer.Requests);
        Assert.Contains(run.Operations, o => o.Action == RestoreAction.InstallDependency && o.Status == RestoreStatus.Applied && o.Verification == VerificationLevel.FunctionallyVerified);
        Assert.All(run.Operations.Where(o => o.Action == RestoreAction.InstallDependency), o => Assert.False(o.RollbackAvailable));

        // Items whose host application is missing stay blocked with a next step.
        Assert.Contains(run.Operations, o => o.Status == RestoreStatus.Blocked && o.NextSteps.Count > 0);

        // Reports exist and hold no secret values.
        Assert.True(File.Exists(run.ReportHtmlPath));
        Assert.True(File.Exists(run.ReportJsonPath));
        foreach (var text in new[] { File.ReadAllText(run.ReportHtmlPath!), File.ReadAllText(run.ReportJsonPath!), string.Concat(h.Journal.Read(run.JobId).Select(r => r.Intent + r.Outcome)) })
        {
            Assert.DoesNotContain("ghp_SIMULATED", text, StringComparison.Ordinal);
        }

        Assert.Null(h.Variable(RegistryHive.CurrentUser, "GITHUB_TOKEN")); // credentials are not carried over by default
        Assert.Equal(JobStates.CompletedWithProblems, h.Journal.GetJob(run.JobId)!.State);
    }

    [Fact]
    public async Task Restoring_twice_changes_nothing_the_second_time()
    {
        var h = new Harness(fixture, "twice");
        await h.RestoreAsync();
        var fingerprint = Fingerprint(h.Target.Root);

        var preflight = await fixture.Planner().PreflightAsync(h.Request(), null, Ct);
        Assert.DoesNotContain(preflight.Effects, o => o.Operation.Action is RestoreAction.CreateFile or RestoreAction.ReplaceFile or RestoreAction.RestoreRepository
            or RestoreAction.SetEnvironmentVariable or RestoreAction.AppendPathEntry or RestoreAction.InstallDependency);

        var (_, second) = await h.RestoreAsync();
        Assert.DoesNotContain(second.Operations, o => o.Status == RestoreStatus.Failed);
        Assert.Equal(fingerprint, Fingerprint(h.Target.Root));
    }

    [Fact]
    public async Task A_plan_that_gained_effects_since_approval_writes_nothing()
    {
        var h = new Harness(fixture, "outdated");
        var request = h.Request();
        var approval = PlanApproval.Approve(await fixture.Planner().PreflightAsync(request, null, Ct));

        // The existing settings file disappears: the merge becomes a new file, an effect nobody approved.
        h.Writer.DeleteFile(VsCodeSettings);
        var before = Fingerprint(h.Target.Root);

        var run = await h.Executor().ExecuteAsync(new RestoreExecutionRequest(request, approval, h.Writer, h.Elevation(), h.Installer, h.Reports), null, Ct);

        Assert.Equal(RestoreRunOutcome.ApprovalOutdated, run.Outcome);
        Assert.Equal(Guid.Empty, run.JobId);
        Assert.Equal(before, Fingerprint(h.Target.Root));
        Assert.Empty(h.Installer.Requests);
    }

    [Fact]
    public async Task Declining_elevation_blocks_only_machine_wide_changes()
    {
        var h = new Harness(fixture, "declined");
        var (_, run) = await h.RestoreAsync(approveElevation: false);

        var machine = Assert.Single(run.Operations, o => o.OperationId.Contains(":env:Machine:DOTNET_CLI_TELEMETRY_OPTOUT", StringComparison.Ordinal));
        Assert.Equal(RestoreStatus.Blocked, machine.Status);
        Assert.Contains("declined", machine.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(h.Variable(RegistryHive.LocalMachine, "DOTNET_CLI_TELEMETRY_OPTOUT"));

        Assert.Equal("code --wait", h.Variable(RegistryHive.CurrentUser, "EDITOR"));
        Assert.Contains("// My new laptop", h.Read(VsCodeSettings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_undoes_that_artifact_only_and_blocks_its_remaining_changes()
    {
        var h = new Harness(fixture, "artifact-failure");
        var preflight = await fixture.Planner().PreflightAsync(h.Request(), null, Ct);

        // An artifact with several new files: fail its last write.
        var group = preflight.Effects.Where(o => o.Operation.Action == RestoreAction.CreateFile && o.Operation.ExpectedTargetState == "absent")
            .GroupBy(o => o.Operation.ArtifactId).First(g => g.Count() >= 2);
        var failing = group.Last().Operation.Target;
        h.Writer.BeforeWrite = path =>
        {
            if (string.Equals(path, failing, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }
        };

        var (_, run) = await h.RestoreAsync();

        var affected = run.Operations.Where(o => o.ArtifactId == group.Key && o.Action == RestoreAction.CreateFile).ToList();
        Assert.All(affected, o => Assert.NotEqual(RestoreStatus.Applied, o.Status));
        Assert.All(group.Where(o => o.Operation.ExpectedTargetState == "absent"), o => Assert.False(h.Exists(o.Operation.Target), o.Operation.Target));
        Assert.Contains(run.Operations, o => o.ArtifactId != group.Key && o.Status == RestoreStatus.Applied);
        Assert.Equal(RestoreRunOutcome.CompletedWithProblems, run.Outcome);
    }

    [Fact]
    public async Task Every_change_is_journaled_before_it_happens()
    {
        var h = new Harness(fixture, "journal-order");
        var violations = new List<string>();
        h.Writer.BeforeWrite = path =>
        {
            var job = h.Journal.ListJobs(JobKinds.Restore, 1).Single();
            var pending = h.Journal.Read(job.JobId).Where(r => r.Outcome is null).ToList();
            if (pending.Count != 1)
            {
                violations.Add($"{path}: {pending.Count} pending intents");
            }
        };

        await h.RestoreAsync();
        Assert.Empty(violations);
    }

    // --- Rollback ---------------------------------------------------------------------------------

    [Fact]
    public async Task Rollback_returns_the_computer_to_its_state_before_the_restore()
    {
        // No installers here: they are the one thing rollback cannot undo.
        var h = new Harness(fixture, "rollback", NoWinGet);
        var before = Fingerprint(h.Target.Root, ignoreRegistryFormatting: true);
        var environment = EnvironmentSnapshot(h.Target);

        var (_, run) = await h.RestoreAsync(h.Request(select: a => a.Record.Artifact.Kind != ArtifactKind.ExtensionInventory));
        Assert.NotEqual(before, Fingerprint(h.Target.Root, ignoreRegistryFormatting: true));

        var rollback = await h.Rollback().RollbackAsync(run.JobId, h.Target, h.Writer, h.Elevation(), Ct);

        Assert.True(rollback.Complete, string.Join("; ", rollback.Failed.Select(f => f.Message)));
        Assert.Empty(rollback.NotReversible);
        Assert.Equal(before, Fingerprint(h.Target.Root, ignoreRegistryFormatting: true));

        Assert.Equal(environment, EnvironmentSnapshot(h.Target));
        Assert.Contains("// My new laptop", h.Read(VsCodeSettings), StringComparison.Ordinal);
        Assert.DoesNotContain("python", h.Read(VsCodeSettings), StringComparison.Ordinal);
        Assert.False(h.Exists(@"C:\Projects\webapp"));
        Assert.Equal(JobStates.RolledBack, h.Journal.GetJob(run.JobId)!.State);
    }

    [Fact]
    public async Task Rollback_keeps_anything_changed_after_the_restore()
    {
        var h = new Harness(fixture, "rollback-conflict", NoWinGet);
        var (_, run) = await h.RestoreAsync(h.Request(select: a => a.Record.Artifact.Kind != ArtifactKind.ExtensionInventory));

        // The user edits a restored file and commits work in a restored repository.
        h.Writer.WriteFileAtomic(@"C:\Users\alex\.gitconfig", Encoding.UTF8.GetBytes("[user]\n\tname = Alex\n"));
        h.Writer.WriteFileAtomic(@"C:\Projects\webapp\NEW-WORK.md", Encoding.UTF8.GetBytes("important"));

        var rollback = await h.Rollback().RollbackAsync(run.JobId, h.Target, h.Writer, h.Elevation(), Ct);

        Assert.False(rollback.Complete);
        Assert.Equal("[user]\n\tname = Alex\n", h.Read(@"C:\Users\alex\.gitconfig"));
        Assert.Equal("important", h.Read(@"C:\Projects\webapp\NEW-WORK.md"));
        Assert.Contains(rollback.Failed, f => f.Message.Contains("changed", StringComparison.Ordinal));

        // Everything else was still undone.
        Assert.DoesNotContain("python", h.Read(VsCodeSettings), StringComparison.Ordinal);
        Assert.Null(h.Variable(RegistryHive.CurrentUser, "EDITOR"));
        Assert.Equal(JobStates.PartiallyRolledBack, h.Journal.GetJob(run.JobId)!.State);
    }

    // --- Recovery ---------------------------------------------------------------------------------

    private sealed class SimulatedPowerLoss : Exception;

    /// <summary>A journal that "loses power" right after the n-th side effect, before its outcome is recorded.</summary>
    private sealed class CrashingJournal(IRestoreJournal inner, int crashAfterOutcomes) : IRestoreJournal
    {
        private int _outcomes;

        public void CreateJob(Guid jobId, string kind, string summary) => inner.CreateJob(jobId, kind, summary);

        public void UpdateJob(Guid jobId, string state, string? summary = null) => inner.UpdateJob(jobId, state, summary);

        public void RecordIntent(Guid jobId, string operationId, int sequence, string intent) => inner.RecordIntent(jobId, operationId, sequence, intent);

        public void RecordOutcome(Guid jobId, string operationId, string outcome)
        {
            if (++_outcomes == crashAfterOutcomes)
            {
                throw new SimulatedPowerLoss();
            }

            inner.RecordOutcome(jobId, operationId, outcome);
        }

        public JournalJob? GetJob(Guid jobId) => inner.GetJob(jobId);

        public IReadOnlyList<JournalJob> ListJobs(string kind, int limit) => inner.ListJobs(kind, limit);

        public IReadOnlyList<JournalRecord> Read(Guid jobId) => inner.Read(jobId);
    }

    [Fact]
    public async Task An_interrupted_restore_is_found_classified_and_can_be_rolled_back()
    {
        var h = new Harness(fixture, "crash", NoWinGet);
        var before = EnvironmentSnapshot(h.Target);
        var request = h.Request(select: a => a.Record.Artifact.Kind != ArtifactKind.ExtensionInventory);

        await Assert.ThrowsAsync<SimulatedPowerLoss>(() => h.RestoreAsync(request, journal: new CrashingJournal(h.Journal, crashAfterOutcomes: 6)));

        var service = h.Rollback();
        var interrupted = Assert.Single(service.FindInterrupted());
        var recovered = service.Recover(interrupted.JobId, h.Target);

        // The change whose outcome was lost is recognized as applied by inspecting the target; nothing is redone.
        var lost = Assert.Single(recovered);
        Assert.Equal(RecoveryClassification.Applied, lost.Classification);
        Assert.Equal(JobStates.Interrupted, h.Journal.GetJob(interrupted.JobId)!.State);
        Assert.Empty(service.FindInterrupted());

        var rollback = await service.RollbackAsync(interrupted.JobId, h.Target, h.Writer, h.Elevation(), Ct);
        Assert.True(rollback.Complete, string.Join("; ", rollback.Failed.Select(f => f.Message)));
        Assert.Equal(6, rollback.Undone.Count);
        Assert.Equal(before, EnvironmentSnapshot(h.Target));
    }

    [Fact]
    public async Task A_change_interrupted_midway_is_uncertain_and_never_rerun()
    {
        var h = new Harness(fixture, "crash-uncertain", NoWinGet);
        var request = h.Request(select: a => a.Record.Artifact.Kind != ArtifactKind.ExtensionInventory);

        await Assert.ThrowsAsync<SimulatedPowerLoss>(() => h.RestoreAsync(request, journal: new CrashingJournal(h.Journal, crashAfterOutcomes: 3)));
        var service = h.Rollback();
        var job = Assert.Single(service.FindInterrupted());
        var pending = h.Journal.Read(job.JobId).Single(r => r.Outcome is null);

        // Something else changed that target before recovery ran.
        var target = System.Text.Json.JsonDocument.Parse(pending.Intent).RootElement.GetProperty("target").GetString()!;
        if (System.Text.Json.JsonDocument.Parse(pending.Intent).RootElement.GetProperty("kind").GetString() == "file")
        {
            h.Writer.WriteFileAtomic(target, "edited by another program"u8.ToArray());
            var recovered = Assert.Single(service.Recover(job.JobId, h.Target));
            Assert.Equal(RecoveryClassification.Uncertain, recovered.Classification);

            var rollback = await service.RollbackAsync(job.JobId, h.Target, h.Writer, h.Elevation(), Ct);
            Assert.Equal("edited by another program", h.Read(target));
            Assert.Contains(rollback.Failed, f => f.OperationId == pending.OperationId);
        }
        else
        {
            Assert.NotEqual(RecoveryClassification.Uncertain, Assert.Single(service.Recover(job.JobId, h.Target)).Classification);
        }
    }

    // --- Helpers ----------------------------------------------------------------------------------

    private static string EnvironmentSnapshot(IMachine machine)
        => string.Join('\n', new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine }
            .SelectMany(h => machine.ReadEnvironment(h).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => $"{h}:{p.Key}={p.Value.AsString()}:{p.Value.Kind}")));

    /// <summary>The fixture's files with their hashes (and, unless ignored, the raw registry file), one per line.</summary>
    private static string Fingerprint(string root, bool ignoreRegistryFormatting = false)
    {
        var lines = Directory.EnumerateFileSystemEntries(Path.Combine(root, "fs"), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(root, p) + (File.Exists(p) ? " " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))[..12] : "\\"));
        if (!ignoreRegistryFormatting)
        {
            lines = lines.Append("registry " + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "registry.json"))))[..12]);
        }

        return string.Join('\n', lines);
    }
}
