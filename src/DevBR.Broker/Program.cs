using System.Globalization;
using System.Security.Principal;
using DevBR.Infrastructure;
using DevBR.Infrastructure.Logging;
using DevBR.Infrastructure.State;
using DevBR.Ipc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Serilog;
using Serilog.Extensions.Logging;

namespace DevBR.Broker;

/// <summary>
/// Elevated broker. Serves exactly one verified DevBR GUI process for one session and exposes only the
/// operations in <see cref="BrokerOperations"/>. Archive parsing never happens here.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBadArguments = 2;
    private const int ExitRejected = 3;
    private const int ExitNotElevated = 5;

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var arguments))
        {
            return ExitBadArguments;
        }

        if (!BuildInfo.IsElevated)
        {
            return ExitNotElevated;
        }

        // Log into the initiating user's profile, not that of the administrator account that approved UAC.
        var paths = new AppPaths(ResolveUserAppDataRoot(arguments.ClientSid));
        paths.EnsureCreated();
        Log.Logger = LogSetup.CreateLogger(paths, ProcessRole.Broker);
        using var loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: true);
        var logger = loggerFactory.CreateLogger("DevBR.Broker");

        try
        {
            // Approved effects come from the initiating user's journal, opened read-only.
            var journal = new SqliteRestoreJournal(new SqliteConnectionStringBuilder { DataSource = paths.StateDatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            var handler = new BrokerOperationHandler(new JournalApprovedPlanStore(journal), new RegistryMachineEnvironment(), () => BuildInfo.IsElevated,
                loggerFactory.CreateLogger<BrokerOperationHandler>());
            var expectedClient = Path.Combine(AppContext.BaseDirectory, "DevBR.exe");

            var server = new IpcServer(
                new IpcServerOptions
                {
                    PipeName = arguments.PipeName,
                    Security = PipeSecurityFactory.Create(arguments.ClientSid),
                    ExpectedTokenSha256 = arguments.TokenSha256,
                    PeerPolicy = new ExpectedPeerPolicy(arguments.ClientPid, expectedClient, arguments.ClientSid),
                    MaxFrameBytes = 64 * 1024,
                    MaxConcurrentRequests = 1,
                    DisconnectOnRejectedRequest = true,
                    ConnectTimeout = TimeSpan.FromSeconds(30),
                    ErrorMapper = BrokerOperationHandler.MapError,
                },
                handler.CreateDispatcher(),
                loggerFactory.CreateLogger<IpcServer>());

            // The session ends when the client disconnects, the client process exits, or after a hard cap.
            using var lifetime = new CancellationTokenSource(TimeSpan.FromHours(4));
            StopWhenClientExits(arguments.ClientPid, lifetime);
            logger.LogInformation("Broker started for client process {ClientPid}.", arguments.ClientPid);

            var outcome = await server.RunAsync(lifetime.Token).ConfigureAwait(false);
            logger.LogInformation("Broker session ended: {Outcome}.", outcome);

            return outcome is IpcSessionOutcome.PeerRejected or IpcSessionOutcome.HandshakeFailed or IpcSessionOutcome.ProtocolViolation
                ? ExitRejected
                : ExitOk;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Broker failed.");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static void StopWhenClientExits(int clientPid, CancellationTokenSource lifetime)
    {
        System.Diagnostics.Process client;
        try
        {
            client = System.Diagnostics.Process.GetProcessById(clientPid);
        }
        catch (ArgumentException)
        {
            lifetime.Cancel();
            return;
        }

        _ = client.WaitForExitAsync().ContinueWith(_ =>
        {
            lifetime.Cancel();
            client.Dispose();
        }, TaskScheduler.Default);
    }

    private static string? ResolveUserAppDataRoot(SecurityIdentifier sid)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid.Value}");
        if (key?.GetValue("ProfileImagePath") is not string profile)
        {
            return null;
        }

        return Path.Combine(Environment.ExpandEnvironmentVariables(profile), "AppData", "Local", "DevBR");
    }

    private sealed record Arguments(string PipeName, int ClientPid, SecurityIdentifier ClientSid, byte[] TokenSha256);

    private static bool TryParseArguments(string[] args, out Arguments arguments)
    {
        arguments = null!;
        if (args.Length != 8 || args[0] != "--pipe" || args[2] != "--client-pid" || args[4] != "--client-sid" || args[6] != "--token-sha256")
        {
            return false;
        }

        var pipe = args[1];
        if (!pipe.StartsWith("DevBR.Broker.", StringComparison.Ordinal) || pipe.Length > 128 || !pipe.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))
        {
            return false;
        }

        if (!int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return false;
        }

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(args[5]);
        }
        catch (ArgumentException)
        {
            return false;
        }

        byte[] hash;
        try
        {
            hash = Convert.FromHexString(args[7]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (hash.Length != 32)
        {
            return false;
        }

        arguments = new Arguments(pipe, pid, sid, hash);
        return true;
    }
}
