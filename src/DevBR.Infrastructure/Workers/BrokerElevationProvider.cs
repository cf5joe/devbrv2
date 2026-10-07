using DevBR.Application.Machine;
using DevBR.Ipc;

namespace DevBR.Infrastructure.Workers;

/// <summary>Obtains administrator rights by starting the broker through UAC, once per restore or rollback.</summary>
public sealed class BrokerElevationProvider(BrokerLauncher launcher) : IElevationProvider
{
    public string? LastMessage { get; private set; }

    public async Task<IElevatedSession?> RequestAsync(CancellationToken cancellationToken)
    {
        var result = await launcher.LaunchAsync(cancellationToken).ConfigureAwait(false);
        LastMessage = result.Message;
        return result is { Outcome: BrokerLaunchOutcome.Connected, Client: { } client } ? new Session(client) : null;
    }

    private sealed class Session(IpcClient client) : IElevatedSession
    {
        public async Task ApplyMachineEnvironmentAsync(Guid jobId, string approvalHash, IReadOnlyList<EnvironmentChange> changes, CancellationToken cancellationToken)
        {
            var request = new ApplyMachineEnvironmentRequest(jobId, approvalHash,
                [.. changes.Select(c => new MachineEnvironmentChange(c.Name, c.ExpectedCurrentValue, c.NewValue, c.Expandable))]);
            try
            {
                await client.RequestAsync<ApplyMachineEnvironmentRequest, ApplyMachineEnvironmentResponse>(BrokerOperations.ApplyMachineEnvironment, request, jobId, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IpcRemoteException ex)
            {
                // Refusals are expected outcomes (unapproved or concurrently changed), reported per operation.
                throw new InvalidOperationException(ex.Message, ex);
            }
            catch (IpcException ex)
            {
                throw new IOException($"The privileged helper stopped responding: {ex.Message}", ex);
            }
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();
    }
}
