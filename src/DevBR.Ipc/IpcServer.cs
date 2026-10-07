using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DevBR.Ipc;

public sealed class IpcServerOptions
{
    public required string PipeName { get; init; }

    public required PipeSecurity Security { get; init; }

    /// <summary>
    /// SHA-256 of the handshake secret the client must present in its hello message. Only the hash is
    /// held by the server, so it can safely travel on a command line.
    /// </summary>
    public required byte[] ExpectedTokenSha256 { get; init; }

    public required IPeerPolicy PeerPolicy { get; init; }

    public int MaxFrameBytes { get; init; } = FrameCodec.DefaultMaxFrameBytes;

    public int MaxConcurrentRequests { get; init; } = 4;

    /// <summary>
    /// When true, any rejected request (unknown operation, malformed payload) ends the session — used by
    /// the elevated broker, which must not tolerate probing.
    /// </summary>
    public bool DisconnectOnRejectedRequest { get; init; }

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for the expected client to connect before giving up.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Translates handler exceptions into errors the client can act on.</summary>
    public Func<Exception, IpcError?>? ErrorMapper { get; init; }
}

public enum IpcSessionOutcome
{
    ClientDisconnected,
    PeerRejected,
    HandshakeFailed,
    ProtocolViolation,
    Cancelled,
}

/// <summary>
/// A single-client named-pipe server. It creates the first and only pipe instance (so the name cannot be
/// pre-squatted), authenticates the connecting process via the OS, then requires a handshake token
/// before dispatching any allow-listed operation.
/// </summary>
public sealed class IpcServer(IpcServerOptions options, IpcDispatcher dispatcher, ILogger logger)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();

    /// <summary>Raised once the pipe exists and is waiting for its client.</summary>
    public event EventHandler? Listening;

    public async Task<IpcSessionOutcome> RunAsync(CancellationToken cancellationToken)
    {
        using var pipe = NamedPipeServerStreamAcl.Create(
            options.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            options.Security);

        Listening?.Invoke(this, EventArgs.Empty);

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(options.ConnectTimeout);
            await pipe.WaitForConnectionAsync(connectTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return IpcSessionOutcome.Cancelled;
        }

        var peer = PeerInspector.DescribeClient(pipe);
        if (!options.PeerPolicy.IsAllowed(peer, out var reason))
        {
            logger.LogWarning("Rejected IPC client process {ProcessId}: {Reason}", peer.ProcessId, reason);
            pipe.Disconnect();
            return IpcSessionOutcome.PeerRejected;
        }

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (!await HandshakeAsync(pipe, sessionCts.Token).ConfigureAwait(false))
            {
                return IpcSessionOutcome.HandshakeFailed;
            }

            return await ServeAsync(pipe, sessionCts).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IpcSessionOutcome.Cancelled;
        }
        catch (IOException)
        {
            return IpcSessionOutcome.ClientDisconnected;
        }
        finally
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);
            foreach (var cts in _inFlight.Values)
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> HandshakeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.HandshakeTimeout);

        IpcEnvelope? hello;
        try
        {
            var frame = await FrameCodec.ReadAsync(pipe, options.MaxFrameBytes, timeout.Token).ConfigureAwait(false);
            hello = frame is null ? null : TryParse(frame);
        }
        catch (Exception ex) when (ex is IpcProtocolException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning("IPC handshake failed: {Reason}", ex.Message);
            return false;
        }

        if (hello is not { V: IpcProtocol.Version, Kind: IpcProtocol.Kinds.Request, Type: IpcProtocol.HelloType, Payload: { } payload })
        {
            logger.LogWarning("IPC client did not start with a valid hello message.");
            await TrySendErrorAsync(pipe, hello?.Id ?? Guid.Empty, IpcProtocol.ErrorCodes.ProtocolViolation, "A hello message was expected.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        HelloRequest? request;
        try
        {
            request = payload.Deserialize<HelloRequest>(IpcJson.Options);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || request.ProtocolVersion != IpcProtocol.Version || !TokenMatches(request.Token))
        {
            logger.LogWarning("IPC client presented an invalid handshake.");
            await TrySendErrorAsync(pipe, hello.Id, IpcProtocol.ErrorCodes.Unauthorized, "The handshake was rejected.", cancellationToken).ConfigureAwait(false);
            return false;
        }

        await SendAsync(pipe, new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Result, hello.Id, IpcProtocol.HelloType, null,
            JsonSerializer.SerializeToElement(new HelloResponse(IpcProtocol.Version, Environment.ProcessId), IpcJson.Options), null), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private bool TokenMatches(string token)
    {
        Span<byte> presented = stackalloc byte[IpcProtocol.TokenBytes];
        if (!Convert.TryFromBase64String(token, presented, out var written) || written != IpcProtocol.TokenBytes)
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(presented, hash);
        return CryptographicOperations.FixedTimeEquals(hash, options.ExpectedTokenSha256);
    }

    private async Task<IpcSessionOutcome> ServeAsync(NamedPipeServerStream pipe, CancellationTokenSource sessionCts)
    {
        var cancellationToken = sessionCts.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? frame;
            try
            {
                frame = await FrameCodec.ReadAsync(pipe, options.MaxFrameBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (IpcProtocolException ex)
            {
                logger.LogWarning("IPC protocol violation: {Reason}", ex.Message);
                await TrySendErrorAsync(pipe, Guid.Empty, IpcProtocol.ErrorCodes.ProtocolViolation, ex.Message, cancellationToken).ConfigureAwait(false);
                return IpcSessionOutcome.ProtocolViolation;
            }

            if (frame is null)
            {
                return IpcSessionOutcome.ClientDisconnected;
            }

            var envelope = TryParse(frame);
            if (envelope is null || envelope.V != IpcProtocol.Version || envelope.Id == Guid.Empty)
            {
                logger.LogWarning("IPC protocol violation: malformed envelope.");
                await TrySendErrorAsync(pipe, envelope?.Id ?? Guid.Empty, IpcProtocol.ErrorCodes.ProtocolViolation, "The message envelope is malformed.", cancellationToken).ConfigureAwait(false);
                return IpcSessionOutcome.ProtocolViolation;
            }

            switch (envelope.Kind)
            {
                case IpcProtocol.Kinds.Cancel:
                    if (_inFlight.TryGetValue(envelope.Id, out var cts))
                    {
                        await cts.CancelAsync().ConfigureAwait(false);
                    }

                    break;

                case IpcProtocol.Kinds.Request:
                    if (!await AcceptRequestAsync(pipe, envelope, cancellationToken).ConfigureAwait(false) && options.DisconnectOnRejectedRequest)
                    {
                        return IpcSessionOutcome.ProtocolViolation;
                    }

                    break;

                default:
                    logger.LogWarning("IPC protocol violation: unexpected message kind {Kind}.", envelope.Kind);
                    await TrySendErrorAsync(pipe, envelope.Id, IpcProtocol.ErrorCodes.ProtocolViolation, "Unexpected message kind.", cancellationToken).ConfigureAwait(false);
                    return IpcSessionOutcome.ProtocolViolation;
            }
        }

        return IpcSessionOutcome.Cancelled;
    }

    /// <returns>False when the request was rejected before reaching a handler.</returns>
    private async Task<bool> AcceptRequestAsync(NamedPipeServerStream pipe, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Type is null || envelope.Type.Length > IpcProtocol.MaxTypeLength || !dispatcher.TryGetHandler(envelope.Type, out var handler))
        {
            logger.LogWarning("Rejected unsupported IPC operation.");
            await SendErrorAsync(pipe, envelope.Id, IpcProtocol.ErrorCodes.UnsupportedOperation, "This operation is not supported.", null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (_inFlight.Count >= options.MaxConcurrentRequests)
        {
            await SendErrorAsync(pipe, envelope.Id, IpcProtocol.ErrorCodes.Busy, "Too many requests are in progress.", null, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_inFlight.TryAdd(envelope.Id, requestCts))
        {
            requestCts.Dispose();
            await SendErrorAsync(pipe, envelope.Id, IpcProtocol.ErrorCodes.ProtocolViolation, "A request with this id is already in progress.", null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Validate the payload synchronously so a strict server can disconnect on malformed input.
        var context = new IpcRequestContext(envelope.Id, envelope.JobId, envelope.Type, progress =>
            SendAsync(pipe, new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Progress, envelope.Id, envelope.Type, envelope.JobId, progress, null), requestCts.Token));

        var work = handler(envelope.Payload, context, requestCts.Token);
        if (work.IsFaulted && work.Exception?.InnerException is IpcPayloadException payloadError)
        {
            _inFlight.TryRemove(envelope.Id, out _);
            requestCts.Dispose();
            logger.LogWarning("Rejected malformed IPC payload for {Type}.", envelope.Type);
            await SendErrorAsync(pipe, envelope.Id, IpcProtocol.ErrorCodes.MalformedPayload, payloadError.Message, null, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _ = CompleteRequestAsync(pipe, envelope, work, requestCts, cancellationToken);
        return true;
    }

    private async Task CompleteRequestAsync(NamedPipeServerStream pipe, IpcEnvelope envelope, Task<JsonElement> work, CancellationTokenSource requestCts, CancellationToken sessionToken)
    {
        try
        {
            var result = await work.ConfigureAwait(false);
            await SendAsync(pipe, new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Result, envelope.Id, envelope.Type, envelope.JobId, result, null), sessionToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCts.IsCancellationRequested)
        {
            await TrySendErrorAsync(pipe, envelope.Id, IpcProtocol.ErrorCodes.Cancelled, "The operation was cancelled.", sessionToken).ConfigureAwait(false);
        }
        catch (IpcPayloadException ex)
        {
            await TrySendErrorAsync(pipe, envelope.Id, IpcProtocol.ErrorCodes.MalformedPayload, ex.Message, sessionToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            var mapped = options.ErrorMapper?.Invoke(ex);
            if (mapped is null)
            {
                logger.LogError(ex, "IPC operation {Type} failed.", envelope.Type);
            }

            var error = mapped ?? new IpcError(IpcProtocol.ErrorCodes.Internal, "The operation failed unexpectedly. See the log for details.", null);
            await TrySendErrorAsync(pipe, envelope.Id, error.Code, error.Message, sessionToken, error.Detail).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The client is gone; the read loop will observe the disconnect.
        }
        finally
        {
            _inFlight.TryRemove(envelope.Id, out _);
            requestCts.Dispose();
        }
    }

    private static IpcEnvelope? TryParse(byte[] frame)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<IpcEnvelope>(frame, IpcJson.Options);
            return envelope is { Kind: not null } ? envelope : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task SendErrorAsync(NamedPipeServerStream pipe, Guid id, string code, string message, string? detail, CancellationToken cancellationToken)
        => SendAsync(pipe, new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Error, id, null, null, null, new IpcError(code, message, detail)), cancellationToken);

    /// <summary>
    /// Best effort, bounded in time: a peer that stops reading must not be able to stall the server
    /// while it reports an error.
    /// </summary>
    private async Task TrySendErrorAsync(NamedPipeServerStream pipe, Guid id, string code, string message, CancellationToken cancellationToken, string? detail = null)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await SendErrorAsync(pipe, id, code, message, detail, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort: the peer may already be gone.
        }
    }

    private async Task SendAsync(NamedPipeServerStream pipe, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJson.Options);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(pipe, bytes, options.MaxFrameBytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
