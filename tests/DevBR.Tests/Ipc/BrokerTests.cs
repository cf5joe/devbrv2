using DevBR.Broker;
using DevBR.Ipc;

namespace DevBR.Tests.Ipc;

public sealed class BrokerTests
{
    private sealed class ApproveEverything : IApprovedPlanStore
    {
        public bool IsApproved(Guid planId, string approvalHash) => true;
    }

    private static BrokerOperationHandler Handler(IApprovedPlanStore? plans = null)
        => new(plans ?? new NoApprovedPlans(), () => false, Loggers.For<BrokerOperationHandler>());

    private static ApplyMachineEnvironmentRequest Request(params MachineEnvironmentChange[] changes)
        => new(Guid.NewGuid(), "approval-hash", changes);

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
    public void Empty_plan_id_is_refused_even_by_a_permissive_store()
    {
        var error = Assert.Throws<BrokerRejectedException>(() =>
            Handler(new ApproveEverything()).ApplyMachineEnvironment(new ApplyMachineEnvironmentRequest(Guid.Empty, "hash", [new("A", null, "1", false)])));
        Assert.Equal(BrokerOperations.PlanNotApprovedCode, error.Code);
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
            Handler(new ApproveEverything()).ApplyMachineEnvironment(Request(new MachineEnvironmentChange(name, null, "value", false))));
        Assert.Equal(BrokerOperations.InvalidChangeCode, error.Code);
    }

    [Fact]
    public void Oversized_or_duplicate_changes_are_refused()
    {
        var handler = Handler(new ApproveEverything());

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
