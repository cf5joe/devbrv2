using DevBR.Application.Machine;
using DevBR.Application.Restore;
using DevBR.Broker;
using DevBR.Ipc;

namespace DevBR.Tests.Ipc;

public sealed class BrokerTests
{
    private sealed class FixedEffects(params string[] effects) : IApprovedPlanStore
    {
        public IReadOnlySet<string>? ApprovedEffects(Guid jobId, string approvalHash) => effects.ToHashSet(StringComparer.Ordinal);
    }

    private sealed class FakeEnvironment : IMachineEnvironment
    {
        public Dictionary<string, (string Value, bool Expandable)> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public (string Value, bool Expandable)? Read(string name) => Values.TryGetValue(name, out var v) ? v : null;

        public void Write(string name, string? value, bool expandable)
        {
            if (value is null)
            {
                Values.Remove(name);
            }
            else
            {
                Values[name] = (value, expandable);
            }
        }
    }

    private static readonly FakeEnvironment SharedEnvironment = new();

    private static BrokerOperationHandler Handler(IApprovedPlanStore? plans = null, FakeEnvironment? environment = null)
        => new(plans ?? new NoApprovedPlans(), environment ?? SharedEnvironment, () => false, Loggers.For<BrokerOperationHandler>());

    private static ApplyMachineEnvironmentRequest Request(params MachineEnvironmentChange[] changes)
        => new(Guid.NewGuid(), "approval-hash", changes);

    private static string SetEffect(string name, string value, string expected = "absent")
        => RestoreEffects.Key("SetEnvironmentVariable", $"ENV:Machine:{name}", "NoConflict", "Elevated", null, RestoreEffects.Sha256(value), expected);

    private static string PathEffect(string entry)
        => RestoreEffects.Key("AppendPathEntry", "PATH (Machine)", "NoConflict", "Elevated", null, null, entry);

    [Fact]
    public void Exposes_only_the_narrow_operation_set()
    {
        var dispatcher = Handler().CreateDispatcher();
        Assert.Equal(
            [BrokerOperations.ApplyMachineEnvironment, BrokerOperations.Status],
            dispatcher.Operations.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Changes_outside_an_approved_plan_are_refused()
    {
        var error = Assert.Throws<BrokerRejectedException>(() =>
            Handler().ApplyMachineEnvironment(Request(new MachineEnvironmentChange("DEVBR_TEST", null, "1", false))));
        Assert.Equal(BrokerOperations.PlanNotApprovedCode, error.Code);
    }

    [Fact]
    public void Empty_job_id_is_refused_even_by_a_permissive_store()
    {
        var error = Assert.Throws<BrokerRejectedException>(() =>
            Handler(new FixedEffects(SetEffect("A", "1"))).ApplyMachineEnvironment(new ApplyMachineEnvironmentRequest(Guid.Empty, "hash", [new("A", null, "1", false)])));
        Assert.Equal(BrokerOperations.PlanNotApprovedCode, error.Code);
    }

    [Fact]
    public void Only_the_exact_approved_value_is_applied()
    {
        var environment = new FakeEnvironment();
        var handler = Handler(new FixedEffects(SetEffect("JAVA_HOME", @"C:\jdk")), environment);

        Assert.Equal(BrokerOperations.PlanNotApprovedCode, Assert.Throws<BrokerRejectedException>(() =>
            handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("JAVA_HOME", null, @"C:\evil", false)))).Code);
        Assert.Equal(BrokerOperations.PlanNotApprovedCode, Assert.Throws<BrokerRejectedException>(() =>
            handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("OTHER", null, @"C:\jdk", false)))).Code);
        Assert.Empty(environment.Values);

        handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("JAVA_HOME", null, @"C:\jdk", false)));
        Assert.Equal(@"C:\jdk", environment.Values["JAVA_HOME"].Value);
    }

    [Fact]
    public void Path_accepts_only_appending_or_removing_an_approved_entry()
    {
        var environment = new FakeEnvironment();
        environment.Values["Path"] = (@"C:\Windows;C:\Tools", true);
        var handler = Handler(new FixedEffects(PathEffect(@"C:\New")), environment);

        // Replacing the whole PATH is not an approved effect.
        Assert.Throws<BrokerRejectedException>(() => handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("Path", @"C:\Windows;C:\Tools", @"C:\New", true))));
        // Appending something else is not either.
        Assert.Throws<BrokerRejectedException>(() => handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("Path", @"C:\Windows;C:\Tools", @"C:\Windows;C:\Tools;C:\Evil", true))));

        handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("Path", @"C:\Windows;C:\Tools", @"C:\Windows;C:\Tools;C:\New", true)));
        Assert.Equal(@"C:\Windows;C:\Tools;C:\New", environment.Values["Path"].Value);

        handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("Path", @"C:\Windows;C:\Tools;C:\New", @"C:\Windows;C:\Tools", true)));
        Assert.Equal(@"C:\Windows;C:\Tools", environment.Values["Path"].Value);
    }

    [Fact]
    public void A_concurrent_change_writes_nothing()
    {
        var environment = new FakeEnvironment();
        environment.Values["JAVA_HOME"] = (@"C:\someone-else", false);
        var handler = Handler(new FixedEffects(SetEffect("JAVA_HOME", @"C:\jdk")), environment);

        var error = Assert.Throws<BrokerRejectedException>(() => handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("JAVA_HOME", null, @"C:\jdk", false))));
        Assert.Equal(BrokerOperations.ConcurrentChangeCode, error.Code);
        Assert.Equal(@"C:\someone-else", environment.Values["JAVA_HOME"].Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A=B")]
    [InlineData("NUL\0NAME")]
    [InlineData("TAB\tNAME")]
    public void Invalid_variable_names_are_refused(string name)
    {
        var error = Assert.Throws<BrokerRejectedException>(() =>
            Handler(new FixedEffects()).ApplyMachineEnvironment(Request(new MachineEnvironmentChange(name, null, "value", false))));
        Assert.Equal(BrokerOperations.InvalidChangeCode, error.Code);
    }

    [Fact]
    public void Oversized_or_duplicate_changes_are_refused()
    {
        var handler = Handler(new FixedEffects());

        Assert.Equal(BrokerOperations.InvalidChangeCode, Assert.Throws<BrokerRejectedException>(() =>
            handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("A", null, new string('x', 40_000), false)))).Code);

        Assert.Equal(BrokerOperations.InvalidChangeCode, Assert.Throws<BrokerRejectedException>(() =>
            handler.ApplyMachineEnvironment(Request(new("Path", null, "a", true), new("PATH", null, "b", true)))).Code);

        Assert.Equal(BrokerOperations.InvalidChangeCode, Assert.Throws<BrokerRejectedException>(() =>
            handler.ApplyMachineEnvironment(Request())).Code);
    }

    [Fact]
    public void Broker_errors_map_to_specific_ipc_codes()
    {
        var mapped = BrokerOperationHandler.MapError(new BrokerRejectedException(BrokerOperations.PlanNotApprovedCode, "nope"));
        Assert.Equal(BrokerOperations.PlanNotApprovedCode, mapped?.Code);
        Assert.Null(BrokerOperationHandler.MapError(new InvalidOperationException("internal")));
    }

    // --- Revalidation against the journal ---------------------------------------------------------

    private sealed class FakeJournal : IRestoreJournal
    {
        public Dictionary<Guid, JournalJob> Jobs { get; } = [];

        public void CreateJob(Guid jobId, string kind, string summary)
            => Jobs[jobId] = new JournalJob(jobId, kind, JobStates.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, summary);

        public void UpdateJob(Guid jobId, string state, string? summary = null)
            => Jobs[jobId] = Jobs[jobId] with { State = state, Summary = summary ?? Jobs[jobId].Summary };

        public void RecordIntent(Guid jobId, string operationId, int sequence, string intent) => throw new NotSupportedException();

        public void RecordOutcome(Guid jobId, string operationId, string outcome) => throw new NotSupportedException();

        public JournalJob? GetJob(Guid jobId) => Jobs.GetValueOrDefault(jobId);

        public IReadOnlyList<JournalJob> ListJobs(string kind, int limit) => [.. Jobs.Values];

        public IReadOnlyList<JournalRecord> Read(Guid jobId) => [];
    }

    private static (FakeJournal Journal, Guid JobId, string Hash) ApprovedJob(params string[] effects)
    {
        var journal = new FakeJournal();
        var jobId = Guid.NewGuid();
        var hash = RestoreEffects.Hash(effects);
        journal.CreateJob(jobId, JobKinds.Restore, new RestoreJobSummary(Guid.NewGuid(), hash, Guid.NewGuid(), "backup.devbr", "PC", effects).ToJson());
        return (journal, jobId, hash);
    }

    [Fact]
    public void The_journal_store_accepts_only_the_exact_job_and_approval_hash()
    {
        var (journal, jobId, hash) = ApprovedJob(SetEffect("JAVA_HOME", @"C:\jdk"));
        var other = ApprovedJob(SetEffect("OTHER", "x"));
        journal.Jobs[other.JobId] = other.Journal.Jobs[other.JobId];
        var environment = new FakeEnvironment();
        var handler = Handler(new JournalApprovedPlanStore(journal), environment);
        var change = new MachineEnvironmentChange("JAVA_HOME", null, @"C:\jdk", false);

        // A different (unknown) job, another approved job, and a tampered or empty approval hash are all refused.
        foreach (var request in new[]
        {
            new ApplyMachineEnvironmentRequest(Guid.NewGuid(), hash, [change]),
            new ApplyMachineEnvironmentRequest(other.JobId, other.Hash, [change]),
            new ApplyMachineEnvironmentRequest(jobId, other.Hash, [change]),
            new ApplyMachineEnvironmentRequest(jobId, hash.ToUpperInvariant(), [change]),
            new ApplyMachineEnvironmentRequest(jobId, hash[..^1] + (hash[^1] == '0' ? '1' : '0'), [change]),
            new ApplyMachineEnvironmentRequest(jobId, " ", [change]),
        })
        {
            Assert.Equal(BrokerOperations.PlanNotApprovedCode, Assert.Throws<BrokerRejectedException>(() => handler.ApplyMachineEnvironment(request)).Code);
        }

        Assert.Empty(environment.Values);
        handler.ApplyMachineEnvironment(new ApplyMachineEnvironmentRequest(jobId, hash, [change]));
        Assert.Equal(@"C:\jdk", environment.Values["JAVA_HOME"].Value);
    }

    [Fact]
    public void Effects_altered_after_approval_or_rolled_back_jobs_are_refused()
    {
        var (journal, jobId, hash) = ApprovedJob(SetEffect("JAVA_HOME", @"C:\jdk"));
        var handler = Handler(new JournalApprovedPlanStore(journal), new FakeEnvironment());
        var change = new ApplyMachineEnvironmentRequest(jobId, hash, [new("JAVA_HOME", null, @"C:\evil", false)]);

        // Someone appends an effect to the stored summary but keeps the old approval hash.
        var summary = RestoreJobSummary.FromJson(journal.Jobs[jobId].Summary)!;
        journal.UpdateJob(jobId, JobStates.Running, (summary with { Effects = [.. summary.Effects, SetEffect("JAVA_HOME", @"C:\evil")] }).ToJson());
        Assert.Equal(BrokerOperations.PlanNotApprovedCode, Assert.Throws<BrokerRejectedException>(() => handler.ApplyMachineEnvironment(change)).Code);

        var rolledBack = ApprovedJob(SetEffect("JAVA_HOME", @"C:\jdk"));
        rolledBack.Journal.UpdateJob(rolledBack.JobId, JobStates.RolledBack);
        Assert.Throws<BrokerRejectedException>(() => Handler(new JournalApprovedPlanStore(rolledBack.Journal), new FakeEnvironment())
            .ApplyMachineEnvironment(new ApplyMachineEnvironmentRequest(rolledBack.JobId, rolledBack.Hash, [new("JAVA_HOME", null, @"C:\jdk", false)])));
    }

    [Fact]
    public void Path_changes_with_extra_or_reordered_entries_are_refused()
    {
        var environment = new FakeEnvironment();
        environment.Values["Path"] = (@"C:\Windows;C:\Tools", true);
        var handler = Handler(new FixedEffects(PathEffect(@"C:\New")), environment);

        foreach (var proposed in new[]
        {
            @"C:\Windows;C:\Tools;C:\New;C:\Evil",
            @"C:\Windows;C:\Tools;C:\Evil;C:\New",
            @"C:\Evil;C:\Windows;C:\Tools;C:\New",
            @"C:\New;C:\Windows;C:\Tools",
            @"C:\Windows;C:\New",
            @"C:\Windows;C:\Tools;C:\NEW",
            @"C:\Windows;C:\Tools;C:\New;",
            @"C:\Windows;C:\Tools;C:\New\..\Evil",
        })
        {
            Assert.Equal(BrokerOperations.PlanNotApprovedCode, Assert.Throws<BrokerRejectedException>(() =>
                handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("Path", @"C:\Windows;C:\Tools", proposed, true)))).Code);
        }

        Assert.Equal(@"C:\Windows;C:\Tools", environment.Values["Path"].Value);
    }

    [Fact]
    public void A_user_scope_or_unelevated_effect_does_not_authorize_a_machine_change()
    {
        var userEffect = RestoreEffects.Key("SetEnvironmentVariable", "ENV:User:JAVA_HOME", "NoConflict", "User", null, RestoreEffects.Sha256(@"C:\jdk"), "absent");
        var notElevated = RestoreEffects.Key("SetEnvironmentVariable", "ENV:Machine:JAVA_HOME", "NoConflict", "User", null, RestoreEffects.Sha256(@"C:\jdk"), "absent");
        var handler = Handler(new FixedEffects(userEffect, notElevated), new FakeEnvironment());

        Assert.Throws<BrokerRejectedException>(() => handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("JAVA_HOME", null, @"C:\jdk", false))));
    }

    [Fact]
    public void An_approved_value_cannot_overwrite_a_state_other_than_the_one_approved()
    {
        // Approved while JAVA_HOME was absent; a request that claims it currently holds a value is not covered.
        var environment = new FakeEnvironment();
        environment.Values["JAVA_HOME"] = (@"C:\users-own-jdk", false);
        var handler = Handler(new FixedEffects(SetEffect("JAVA_HOME", @"C:\jdk")), environment);

        var error = Assert.Throws<BrokerRejectedException>(() =>
            handler.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("JAVA_HOME", @"C:\users-own-jdk", @"C:\jdk", false))));
        Assert.Equal(BrokerOperations.PlanNotApprovedCode, error.Code);
        Assert.Equal(@"C:\users-own-jdk", environment.Values["JAVA_HOME"].Value);

        // Approved as a replacement of a specific previous value: only that value may be replaced.
        var replace = Handler(new FixedEffects(SetEffect("JAVA_HOME", @"C:\jdk", $"value:{RestoreEffects.Sha256(@"C:\old")[..16]}")), environment);
        Assert.Throws<BrokerRejectedException>(() =>
            replace.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("JAVA_HOME", @"C:\users-own-jdk", @"C:\jdk", false))));

        environment.Values["JAVA_HOME"] = (@"C:\old", false);
        replace.ApplyMachineEnvironment(Request(new MachineEnvironmentChange("JAVA_HOME", @"C:\old", @"C:\jdk", false)));
        Assert.Equal(@"C:\jdk", environment.Values["JAVA_HOME"].Value);
    }

    [Fact]
    public void Replaying_an_applied_request_writes_nothing()
    {
        var environment = new FakeEnvironment();
        var handler = Handler(new FixedEffects(SetEffect("JAVA_HOME", @"C:\jdk")), environment);
        var request = Request(new MachineEnvironmentChange("JAVA_HOME", null, @"C:\jdk", false));

        handler.ApplyMachineEnvironment(request);
        environment.Values["JAVA_HOME"] = (@"C:\changed-later", false);

        Assert.Throws<BrokerRejectedException>(() => handler.ApplyMachineEnvironment(request));
        Assert.Equal(@"C:\changed-later", environment.Values["JAVA_HOME"].Value);
    }

    [Fact]
    public void Only_refusals_that_indicate_probing_end_the_session()
    {
        Assert.True(BrokerOperationHandler.IsRejection(new BrokerRejectedException(BrokerOperations.PlanNotApprovedCode, "x")));
        Assert.True(BrokerOperationHandler.IsRejection(new BrokerRejectedException(BrokerOperations.InvalidChangeCode, "x")));
        Assert.False(BrokerOperationHandler.IsRejection(new BrokerRejectedException(BrokerOperations.ConcurrentChangeCode, "x")));
        Assert.False(BrokerOperationHandler.IsRejection(new IOException("registry")));
    }
}
