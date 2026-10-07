using DevBR.Application.Machine;
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
}
