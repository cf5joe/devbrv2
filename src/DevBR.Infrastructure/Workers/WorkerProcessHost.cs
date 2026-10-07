using System.Diagnostics;
using DevBR.Ipc;
using Microsoft.Extensions.Logging;

namespace DevBR.Infrastructure.Workers;

public sealed class WorkerHostOptions
{
    public string ExecutablePath { get; init; } = Path.Combine(AppContext.BaseDirectory, "DevBR.ArchiveWorker.exe");

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class WorkerFaultedEventArgs(int? exitCode, string message) : EventArgs
{
    public int? ExitCode { get; } = exitCode;

    public string Message { get; } = message;
}

/// <summary>
/// Owns the unelevated archive worker process. The worker starts lazily on first use, and a crashed
/// worker is reported through <see cref="Faulted"/> and replaced on the next request. The GUI never
/// loads the native archive library itself.
/// </summary>
public sealed class WorkerProcessHost(WorkerHostOptions options, ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<WorkerProcessHost>();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private IpcClient? _client;
    private bool _disposing;

    public event EventHandler<WorkerFaultedEventArgs>? Faulted;

    public int? ProcessId => _client?.IsConnected == true ? _client.ServerProcessId : null;

    public async Task<IpcClient> GetClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposing, this);

        if (_client is { IsConnected: true } existing)
        {
            return existing;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { IsConnected: true } raced)
            {
                return raced;
            }

            await StopAsync().ConfigureAwait(false);
            _client = await StartAsync(cancellationToken).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<IpcClient> StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ExecutablePath))
        {
            throw new FileNotFoundException("The archive worker executable is missing from the DevBR folder.", options.ExecutablePath);
        }

        var token = HandshakeToken.Create();
        var pipeName = PipeSecurityFactory.NewPipeName("Worker");

        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath)!,
        };
        startInfo.ArgumentList.Add("--pipe");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The archive worker could not be started.");
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { _logger.LogDebug("worker: {Line}", e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { _logger.LogWarning("worker stderr: {Line}", e.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        // The token travels over the private stdin pipe, never on the command line.
        await process.StandardInput.WriteLineAsync(Convert.ToBase64String(token).AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        // Stop waiting immediately if the worker dies during startup.
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        EventHandler onExit = (_, _) => startup.Cancel();
        process.Exited += onExit;
        if (process.HasExited)
        {
            await startup.CancelAsync().ConfigureAwait(false);
        }

        IpcClient client;
        try
        {
            client = await IpcClient.ConnectAsync(
                pipeName, token, new ExpectedPeerPolicy(process.Id, options.ExecutablePath), options.ConnectTimeout,
                loggerFactory.CreateLogger<IpcClient>(), startup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (process.HasExited && !cancellationToken.IsCancellationRequested)
        {
            throw new IpcDisconnectedException($"The archive worker exited during startup (exit code {process.ExitCode}).");
        }
        catch
        {
            TryKill(process);
            throw;
        }
        finally
        {
            process.Exited -= onExit;
        }

        client.Disconnected += (_, failure) => OnDisconnected(process, failure);
        _logger.LogInformation("Archive worker started (pid {ProcessId}).", process.Id);
        return client;
    }

    private void OnDisconnected(Process process, Exception? failure)
    {
        // Intentional shutdowns detach the process first, so only unexpected exits are reported.
        if (_disposing || !ReferenceEquals(Volatile.Read(ref _process), process))
        {
            return;
        }

        int? exitCode = null;
        try
        {
            if (process.WaitForExit(TimeSpan.FromSeconds(2)))
            {
                exitCode = process.ExitCode;
            }
        }
        catch (InvalidOperationException)
        {
        }

        _logger.LogError(failure, "Archive worker {ProcessId} stopped unexpectedly (exit code {ExitCode}).", process.Id, exitCode);
        Faulted?.Invoke(this, new WorkerFaultedEventArgs(exitCode, "The background archive process stopped unexpectedly. DevBR will start a new one for the next operation."));
    }

    private async Task StopAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        var process = Interlocked.Exchange(ref _process, null);

        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        if (process is not null)
        {
            // Closing the pipe ends the worker; kill it only if it does not exit promptly.
            if (!process.WaitForExit(TimeSpan.FromSeconds(3)))
            {
                TryKill(process);
            }

            process.Dispose();
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not stop the archive worker.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposing = true;
        await StopAsync().ConfigureAwait(false);
        _startLock.Dispose();
    }
}
