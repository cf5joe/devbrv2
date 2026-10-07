using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using DevBR.Ipc;
using Microsoft.Extensions.Logging;

namespace DevBR.Infrastructure.Workers;

public enum BrokerLaunchOutcome
{
    Connected,
    DeclinedByUser,
    Failed,
}

public sealed record BrokerLaunchResult(BrokerLaunchOutcome Outcome, IpcClient? Client, BrokerStatus? Status, string Message);

/// <summary>
/// Starts the elevated broker through UAC only when a privileged step is actually needed. Declining UAC
/// is a normal outcome: user-level work continues and privileged steps are marked blocked.
/// </summary>
public sealed class BrokerLauncher(ILoggerFactory loggerFactory)
{
    private const int ErrorCancelled = 1223;

    private readonly ILogger _logger = loggerFactory.CreateLogger<BrokerLauncher>();

    public string ExecutablePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "DevBR.Broker.exe");

    public async Task<BrokerLaunchResult> LaunchAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ExecutablePath))
        {
            return new BrokerLaunchResult(BrokerLaunchOutcome.Failed, null, null, "The privileged helper is missing from the DevBR folder.");
        }

        var token = HandshakeToken.Create();
        var pipeName = PipeSecurityFactory.NewPipeName("Broker");
        using var identity = WindowsIdentity.GetCurrent();

        var startInfo = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
        };

        // Only the token's hash is passed on the command line; the token itself goes over the pipe.
        // The initiating user's SID lets the broker keep acting for this user even when UAC
        // elevation uses a different administrator account.
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--client-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--client-sid");
        startInfo.ArgumentList.Add(identity.User!.Value);
        startInfo.ArgumentList.Add("--token-sha256");
        startInfo.ArgumentList.Add(Convert.ToHexString(HandshakeToken.Hash(token)));

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _logger.LogInformation("The user declined elevation for the privileged helper.");
            return new BrokerLaunchResult(BrokerLaunchOutcome.DeclinedByUser, null, null, "Administrator approval was declined. Privileged steps are blocked; other work can continue.");
        }

        if (process is null)
        {
            return new BrokerLaunchResult(BrokerLaunchOutcome.Failed, null, null, "The privileged helper did not start.");
        }

        try
        {
            var client = await IpcClient.ConnectAsync(
                pipeName, token, new ExpectedPeerPolicy(process.Id, ExecutablePath), TimeSpan.FromSeconds(20),
                loggerFactory.CreateLogger<IpcClient>(), cancellationToken).ConfigureAwait(false);

            var status = await client.RequestAsync<BrokerStatus>(BrokerOperations.Status, null, null, cancellationToken).ConfigureAwait(false);
            return new BrokerLaunchResult(BrokerLaunchOutcome.Connected, client, status, "The privileged helper is running.");
        }
        catch (Exception ex) when (ex is IpcException or TimeoutException or IOException)
        {
            _logger.LogError(ex, "Could not connect to the privileged helper.");
            try
            {
                process.Kill();
            }
            catch (Exception killError) when (killError is InvalidOperationException or Win32Exception)
            {
            }

            return new BrokerLaunchResult(BrokerLaunchOutcome.Failed, null, null, "The privileged helper started but could not be verified, so it was stopped.");
        }
        finally
        {
            process.Dispose();
        }
    }
}
