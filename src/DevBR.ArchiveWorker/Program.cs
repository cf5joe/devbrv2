using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using DevBR.Application.Archive;
using DevBR.Archive;
using DevBR.Infrastructure;
using DevBR.Infrastructure.Logging;
using DevBR.Infrastructure.Workers;
using DevBR.Ipc;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace DevBR.ArchiveWorker;

/// <summary>
/// Unelevated, single-client archive worker. It serves exactly one GUI process (its parent) and exits
/// when that connection ends or the parent exits, so native-library failures stay out of the GUI.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitBadArguments = 2;
    private const int ExitRejected = 3;

    public static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var pipeName, out var parentPid))
        {
            return ExitBadArguments;
        }

        // The handshake token arrives over the private stdin pipe, not the command line.
        var tokenLine = await ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        byte[] token;
        try
        {
            token = Convert.FromBase64String(tokenLine ?? string.Empty);
        }
        catch (FormatException)
        {
            return ExitBadArguments;
        }

        if (token.Length != IpcProtocol.TokenBytes)
        {
            return ExitBadArguments;
        }

        var paths = new AppPaths();
        paths.EnsureCreated();
        Log.Logger = LogSetup.CreateLogger(paths, ProcessRole.Worker);
        using var loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: true);
        var logger = loggerFactory.CreateLogger("DevBR.ArchiveWorker");

        using var lifetime = new CancellationTokenSource();
        try
        {
            WatchParent(parentPid, lifetime, logger);

            var archive = new SevenZipArchiveService(loggerFactory.CreateLogger<SevenZipArchiveService>());
            var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";

            var dispatcher = new IpcDispatcher()
                .Register(WorkerOperations.Ping, (_, _) => Task.FromResult(new WorkerStatus(Environment.ProcessId, version)))
                .Register<ArchiveCreateRequest, ArchiveCreateResult>(WorkerOperations.CreateArchive, (request, context, ct) =>
                    archive.CreateAsync(request, ProgressSink(context), ct))
                .Register<ArchiveInspectRequest, ArchiveInspection>(WorkerOperations.InspectArchive, (request, _, ct) =>
                    archive.InspectAsync(request, ct))
                .Register<ArchiveExtractRequest, ArchiveExtractResult>(WorkerOperations.ExtractArchive, (request, context, ct) =>
                    archive.ExtractSelectedAsync(request, ProgressSink(context), ct))
                .Register<SpooledExtractRequest, SpooledExtractResult>(SpooledExtract.Operation, (request, context, ct) =>
                    SpooledExtract.RunAsync(archive, request, ProgressSink(context), ct));

            using var identity = WindowsIdentity.GetCurrent();
            var server = new IpcServer(
                new IpcServerOptions
                {
                    PipeName = pipeName,
                    Security = PipeSecurityFactory.Create(identity.User!),
                    ExpectedTokenSha256 = HandshakeToken.Hash(token),
                    PeerPolicy = new ExpectedPeerPolicy(parentPid, expectedUser: identity.User),
                    ErrorMapper = ex => ex is ArchiveException archiveError
                        ? new IpcError(WorkerOperations.ArchiveErrorCode, archiveError.Message, archiveError.Kind.ToString())
                        : null,
                },
                dispatcher,
                loggerFactory.CreateLogger<IpcServer>());

            logger.LogInformation("Archive worker listening for parent {ParentPid}.", parentPid);
            var outcome = await server.RunAsync(lifetime.Token).ConfigureAwait(false);
            logger.LogInformation("Archive worker session ended: {Outcome}.", outcome);

            return outcome is IpcSessionOutcome.PeerRejected or IpcSessionOutcome.HandshakeFailed ? ExitRejected : ExitOk;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Archive worker failed.");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Forwards progress to the client at most every 100 ms; the final state is always sent.</summary>
    private static IProgress<ArchiveProgress> ProgressSink(IpcRequestContext context)
    {
        var gate = new Lock();
        var lastSent = Stopwatch.StartNew();
        string? lastStage = null;
        return new SynchronousProgress<ArchiveProgress>(value =>
        {
            lock (gate)
            {
                if (value.Stage == lastStage && value.Percent is not 100 && lastSent.ElapsedMilliseconds < 100)
                {
                    return;
                }

                lastStage = value.Stage;
                lastSent.Restart();
            }

            _ = context.ReportProgressAsync(value);
        });
    }

    private static void WatchParent(int parentPid, CancellationTokenSource lifetime, Microsoft.Extensions.Logging.ILogger logger)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("Parent process {ParentPid} is not running.", parentPid);
            lifetime.Cancel();
            return;
        }

        _ = parent.WaitForExitAsync().ContinueWith(_ =>
        {
            logger.LogInformation("Parent process exited; stopping.");
            lifetime.Cancel();
            parent.Dispose();
        }, TaskScheduler.Default);
    }

    private static bool TryParseArguments(string[] args, out string pipeName, out int parentPid)
    {
        pipeName = string.Empty;
        parentPid = 0;

        if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--parent-pid")
        {
            return false;
        }

        pipeName = args[1];
        return pipeName.StartsWith("DevBR.Worker.", StringComparison.Ordinal)
            && pipeName.Length <= 128
            && pipeName.All(c => char.IsAsciiLetterOrDigit(c) || c == '.')
            && int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid)
            && parentPid > 0;
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await Console.In.ReadLineAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
