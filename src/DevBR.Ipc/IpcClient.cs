using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DevBR.Ipc;

/// <summary>
/// Client side of the protocol. Verifies the server process via the OS before sending anything, then
/// multiplexes requests, progress and cancellation over one pipe. When the server dies, every pending
/// request fails with <see cref="IpcDisconnectedException"/> instead of hanging.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ILogger _logger;
    private readonly int _maxFrameBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _readLoop;
    private int _disconnected;

    private IpcClient(NamedPipeClientStream pipe, ILogger logger, int maxFrameBytes)
    {
        _pipe = pipe;
        _logger = logger;
        _maxFrameBytes = maxFrameBytes;
    }

    /// <summary>Raised once, from a background thread, when the connection is lost.</summary>
    public event EventHandler<Exception?>? Disconnected;

    public bool IsConnected => Volatile.Read(ref _disconnected) == 0 && _pipe.IsConnected;

    public int ServerProcessId { get; private set; }

    public static async Task<IpcClient> ConnectAsync(
        string pipeName,
        byte[] token,
        IPeerPolicy serverPolicy,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken,
        int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes)
    {
        // Identification only: the server may learn who we are but may not act as us.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(timeout, cancellationToken).ConfigureAwait(false);

            var server = PeerInspector.DescribeServer(pipe);
            if (!serverPolicy.IsAllowed(server, out var reason))
            {
                throw new IpcPeerRejectedException($"The IPC server could not be verified: {reason}.");
            }

            var client = new IpcClient(pipe, logger, maxFrameBytes) { ServerProcessId = server.ProcessId };
            client._readLoop = Task.Run(client.ReadLoopAsync, CancellationToken.None);

            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(timeout);
            try
            {
                var hello = await client.RequestAsync<HelloRequest, HelloResponse>(
                    IpcProtocol.HelloType, new HelloRequest(IpcProtocol.Version, Convert.ToBase64String(token)), null, null, handshakeTimeout.Token).ConfigureAwait(false);

                if (hello.ProtocolVersion != IpcProtocol.Version || hello.ServerProcessId != server.ProcessId)
                {
                    throw new IpcPeerRejectedException("The IPC server returned an unexpected handshake.");
                }
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            return client;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<TResponse> RequestAsync<TResponse>(string type, Guid? jobId, Action<JsonElement>? onProgress, CancellationToken cancellationToken)
        => RequestCoreAsync<TResponse>(type, null, jobId, onProgress, cancellationToken);

    public Task<TResponse> RequestAsync<TRequest, TResponse>(string type, TRequest payload, Guid? jobId, Action<JsonElement>? onProgress, CancellationToken cancellationToken)
        => RequestCoreAsync<TResponse>(type, JsonSerializer.SerializeToElement(payload, IpcJson.Options), jobId, onProgress, cancellationToken);

    private async Task<TResponse> RequestCoreAsync<TResponse>(string type, JsonElement? payload, Guid? jobId, Action<JsonElement>? onProgress, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new IpcDisconnectedException("The connection to the background process is closed.");
        }

        var id = Guid.NewGuid();
        var pending = new Pending(onProgress);
        _pending[id] = pending;

        await using var registration = cancellationToken.Register(() =>
        {
            if (pending.Completion.TrySetCanceled(cancellationToken))
            {
                _ = TrySendAsync(new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Cancel, id, type, jobId, null, null));
            }
        }).ConfigureAwait(false);

        try
        {
            await SendAsync(new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Request, id, type, jobId, payload, null), cancellationToken).ConfigureAwait(false);
            var result = await pending.Completion.Task.ConfigureAwait(false);
            return result.Deserialize<TResponse>(IpcJson.Options)
                ?? throw new IpcProtocolException($"The response to '{type}' was empty.");
        }
        catch (IOException ex)
        {
            throw new IpcDisconnectedException("The connection to the background process was lost.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadAsync(_pipe, _maxFrameBytes, _lifetime.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                var envelope = JsonSerializer.Deserialize<IpcEnvelope>(frame, IpcJson.Options);
                if (envelope is null || envelope.V != IpcProtocol.Version)
                {
                    throw new IpcProtocolException("The server sent a malformed message.");
                }

                if (!_pending.TryGetValue(envelope.Id, out var pending))
                {
                    if (envelope.Kind == IpcProtocol.Kinds.Error && envelope.Error is { } orphan)
                    {
                        _logger.LogWarning("IPC server reported {Code}: {Message}", orphan.Code, orphan.Message);
                    }

                    continue;
                }

                switch (envelope.Kind)
                {
                    case IpcProtocol.Kinds.Result:
                        pending.Completion.TrySetResult(envelope.Payload ?? default);
                        break;
                    case IpcProtocol.Kinds.Error:
                        pending.Completion.TrySetException(new IpcRemoteException(envelope.Error ?? new IpcError(IpcProtocol.ErrorCodes.Internal, "Unknown error.", null)));
                        break;
                    case IpcProtocol.Kinds.Progress when envelope.Payload is { } progress:
                        try
                        {
                            pending.OnProgress?.Invoke(progress);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "A progress handler failed.");
                        }

                        break;
                    default:
                        throw new IpcProtocolException($"The server sent an unexpected message kind '{envelope.Kind}'.");
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        OnDisconnected(failure);
    }

    private void OnDisconnected(Exception? failure)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1)
        {
            return;
        }

        var error = new IpcDisconnectedException("The background process stopped responding or exited.", failure);
        foreach (var pending in _pending.Values)
        {
            pending.Completion.TrySetException(error);
        }

        Disconnected?.Invoke(this, failure);
    }

    private async Task SendAsync(IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJson.Options);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_pipe, bytes, _maxFrameBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task TrySendAsync(IpcEnvelope envelope)
    {
        try
        {
            await SendAsync(envelope, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            await _readLoop.ConfigureAwait(false);
        }

        OnDisconnected(null);
        _lifetime.Dispose();
    }

    private sealed class Pending(Action<JsonElement>? onProgress)
    {
        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<JsonElement>? OnProgress { get; } = onProgress;
    }
}
