using DevBR.Application.Machine;
using DevBR.Application.Restore;
using DevBR.Ipc;
using Microsoft.Extensions.Logging;
using Win32 = Microsoft.Win32;

namespace DevBR.Broker;

/// <summary>The effects of restore jobs the user approved in the GUI, as independently revalidated by the broker.</summary>
public interface IApprovedPlanStore
{
    /// <returns>The approved effects, or null when the job is unknown, finished with, or does not match the approval hash.</returns>
    IReadOnlySet<string>? ApprovedEffects(Guid jobId, string approvalHash);
}

/// <summary>Refuses everything (used when the journal cannot be opened).</summary>
public sealed class NoApprovedPlans : IApprovedPlanStore
{
    public IReadOnlySet<string>? ApprovedEffects(Guid jobId, string approvalHash) => null;
}

/// <summary>Reads approved effects from the initiating user's restore journal.</summary>
public sealed class JournalApprovedPlanStore(IRestoreJournal journal) : IApprovedPlanStore
{
    public IReadOnlySet<string>? ApprovedEffects(Guid jobId, string approvalHash)
    {
        var summary = RestoreJobSummary.FromJson(journal.GetJob(jobId)?.Summary);
        return summary is not null && string.Equals(summary.ApprovalHash, approvalHash, StringComparison.Ordinal)
            ? RestoreJobSummary.ApprovedEffects(journal, jobId)
            : null;
    }
}

/// <summary>Machine-scope environment variables (HKLM\…\Session Manager\Environment).</summary>
public interface IMachineEnvironment
{
    (string Value, bool Expandable)? Read(string name);

    void Write(string name, string? value, bool expandable);
}

public sealed class RegistryMachineEnvironment : IMachineEnvironment
{
    private const string Key = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public (string Value, bool Expandable)? Read(string name)
    {
        using var key = Win32.Registry.LocalMachine.OpenSubKey(Key);
        if (key?.GetValue(name, null, Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
        {
            return null;
        }

        return (value, key.GetValueKind(name) == Win32.RegistryValueKind.ExpandString);
    }

    public void Write(string name, string? value, bool expandable)
    {
        using var key = Win32.Registry.LocalMachine.OpenSubKey(Key, writable: true) ?? throw new InvalidOperationException("The machine environment key is missing.");
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, expandable ? Win32.RegistryValueKind.ExpandString : Win32.RegistryValueKind.String);
        }
    }
}

public sealed class BrokerRejectedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Implements the broker's narrow operation set. Inputs are validated here even though the GUI has
/// already validated them: the broker trusts nothing it receives. A machine environment change is
/// applied only when it matches an effect of an approved restore job and the registry still holds the
/// value the GUI saw.
/// </summary>
public sealed class BrokerOperationHandler(IApprovedPlanStore plans, IMachineEnvironment environment, Func<bool> isElevated, ILogger<BrokerOperationHandler> logger)
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

        var effects = request.JobId == Guid.Empty || string.IsNullOrWhiteSpace(request.ApprovalHash) ? null : plans.ApprovedEffects(request.JobId, request.ApprovalHash);
        if (effects is null)
        {
            logger.LogWarning("Refused machine environment change for unapproved job {JobId}.", request.JobId);
            throw new BrokerRejectedException(BrokerOperations.PlanNotApprovedCode, "This change is not part of an approved restore plan.");
        }

        foreach (var change in request.Changes)
        {
            if (!RestoreEffects.Permits(effects, new EnvironmentChange(change.Name, change.ExpectedCurrentValue, change.NewValue, change.Expandable)))
            {
                logger.LogWarning("Refused a change to {Name} that is not among the approved effects of job {JobId}.", change.Name, request.JobId);
                throw new BrokerRejectedException(BrokerOperations.PlanNotApprovedCode, $"The change to {change.Name} is not part of the approved plan.");
            }
        }

        // All or nothing: if any variable changed since the GUI looked, nothing is written.
        foreach (var change in request.Changes)
        {
            if (!string.Equals(environment.Read(change.Name)?.Value, change.ExpectedCurrentValue, StringComparison.Ordinal))
            {
                throw new BrokerRejectedException(BrokerOperations.ConcurrentChangeCode, $"{change.Name} changed since it was checked; nothing was written.");
            }
        }

        var applied = new List<string>();
        foreach (var change in request.Changes)
        {
            environment.Write(change.Name, change.NewValue, change.Expandable);
            applied.Add(change.Name);
            logger.LogInformation("Applied approved machine environment change to {Name} for job {JobId}.", change.Name, request.JobId);
        }

        return new ApplyMachineEnvironmentResponse(applied);
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

            if ((change.NewValue?.Length ?? 0) > MaxValueLength || (change.NewValue?.Contains('\0') ?? false) ||
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
