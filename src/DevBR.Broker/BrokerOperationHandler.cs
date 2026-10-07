using DevBR.Ipc;
using Microsoft.Extensions.Logging;

namespace DevBR.Broker;

/// <summary>Plans the user approved in the GUI, as independently revalidated by the broker.</summary>
public interface IApprovedPlanStore
{
    bool IsApproved(Guid planId, string approvalHash);
}

/// <summary>
/// Phase 1 has no restore planner, so no plan can be approved yet and every privileged change is
/// refused. Phase 4/5 replace this with plans read from the journal and revalidated here.
/// </summary>
public sealed class NoApprovedPlans : IApprovedPlanStore
{
    public bool IsApproved(Guid planId, string approvalHash) => false;
}

public sealed class BrokerRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Implements the broker's narrow operation set. Inputs are validated here even though the GUI has
/// already validated them: the broker trusts nothing it receives.
/// </summary>
public sealed class BrokerOperationHandler(IApprovedPlanStore plans, Func<bool> isElevated, ILogger<BrokerOperationHandler> logger)
{
    public const int MaxChangesPerRequest = 256;
    public const int MaxNameLength = 255;
    public const int MaxValueLength = 32767;

    public IpcDispatcher CreateDispatcher()
    {
        var dispatcher = new IpcDispatcher();
        dispatcher
            .Register(BrokerOperations.Status, (_, _) => Task.FromResult(GetStatus(dispatcher)))
            .Register<ApplyMachineEnvironmentRequest, ApplyMachineEnvironmentResponse>(BrokerOperations.ApplyMachineEnvironment, (request, _, _) =>
                Task.FromResult(ApplyMachineEnvironment(request)));
        return dispatcher;
    }

    public static IpcError? MapError(Exception exception)
        => exception is BrokerRejectedException rejected ? new IpcError(rejected.Code, rejected.Message, null) : null;

    private BrokerStatus GetStatus(IpcDispatcher dispatcher)
        => new(isElevated(), IpcProtocol.Version, [.. dispatcher.Operations.Order(StringComparer.Ordinal)]);

    public ApplyMachineEnvironmentResponse ApplyMachineEnvironment(ApplyMachineEnvironmentRequest request)
    {
        ValidateChanges(request.Changes);

        if (request.PlanId == Guid.Empty || string.IsNullOrWhiteSpace(request.ApprovalHash) || !plans.IsApproved(request.PlanId, request.ApprovalHash))
        {
            logger.LogWarning("Refused machine environment change for unapproved plan {PlanId}.", request.PlanId);
            throw new BrokerRejectedException(BrokerOperations.PlanNotApprovedCode, "This change is not part of an approved restore plan.");
        }

        // Reached only with an approved plan, which Phase 1 cannot produce.
        throw new BrokerRejectedException(BrokerOperations.PlanNotApprovedCode, "Machine environment changes are not available in this build.");
    }

    private static void ValidateChanges(IReadOnlyList<MachineEnvironmentChange> changes)
    {
        if (changes.Count is 0 or > MaxChangesPerRequest)
        {
            throw new BrokerRejectedException(BrokerOperations.InvalidChangeCode, $"A request must contain between 1 and {MaxChangesPerRequest} changes.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Name) || change.Name.Length > MaxNameLength ||
                change.Name.AsSpan().ContainsAny('=', '\0') || change.Name.Any(char.IsControl))
            {
                throw new BrokerRejectedException(BrokerOperations.InvalidChangeCode, "An environment variable name is invalid.");
            }

            if (change.NewValue.Length > MaxValueLength || change.NewValue.Contains('\0') ||
                (change.ExpectedCurrentValue?.Contains('\0') ?? false))
            {
                throw new BrokerRejectedException(BrokerOperations.InvalidChangeCode, $"The value for '{change.Name}' is invalid.");
            }

            if (!names.Add(change.Name))
            {
                throw new BrokerRejectedException(BrokerOperations.InvalidChangeCode, $"'{change.Name}' appears more than once.");
            }
        }
    }
}
