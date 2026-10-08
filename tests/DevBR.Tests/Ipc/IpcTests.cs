using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DevBR.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Tests.Ipc;

public sealed record EchoRequest(string Text);

public sealed record EchoResponse(string Text);

public sealed class IpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static IpcDispatcher CreateDispatcher(TaskCompletionSource<bool>? cancelled = null) => new IpcDispatcher()
        .Register<EchoRequest, EchoResponse>("test.echo", (request, _, _) => Task.FromResult(new EchoResponse(request.Text)))
        .Register<EchoRequest, EchoResponse>("test.progress", async (request, context, _) =>
        {
            for (var i = 1; i <= 3; i++)
            {
                await context.ReportProgressAsync(new EchoResponse($"{request.Text}{i}"));
            }

            return new EchoResponse("done");
        })
        .Register("test.wait", async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled?.TrySetResult(true);
                throw;
            }

            return Empty.Value;
        });

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public Harness(IpcDispatcher dispatcher, IPeerPolicy? policy = null, bool strict = false, int maxFrame = FrameCodec.DefaultMaxFrameBytes)
        {
            Token = HandshakeToken.Create();
            PipeName = $"DevBR.Test.{Guid.NewGuid():N}";
            using var identity = WindowsIdentity.GetCurrent();
            var server = new IpcServer(
                new IpcServerOptions
                {
                    PipeName = PipeName,
                    Security = PipeSecurityFactory.Create(identity.User!),
                    ExpectedTokenSha256 = HandshakeToken.Hash(Token),
                    PeerPolicy = policy ?? new ExpectedPeerPolicy(Environment.ProcessId, expectedUser: identity.User),
                    DisconnectOnRejectedRequest = strict,
                    MaxFrameBytes = maxFrame,
                    HandshakeTimeout = TimeSpan.FromSeconds(3),
                },
                dispatcher,
                NullLogger.Instance);

            var listening = new TaskCompletionSource();
            server.Listening += (_, _) => listening.TrySetResult();
            Outcome = Task.Run(() => server.RunAsync(_cts.Token));
            listening.Task.Wait(TimeSpan.FromSeconds(5));
        }

        public byte[] Token { get; }

        public string PipeName { get; }

        public Task<IpcSessionOutcome> Outcome { get; }

        public Task<IpcClient> ConnectAsync(byte[]? token = null)
            => IpcClient.ConnectAsync(PipeName, token ?? Token, new ExpectedPeerPolicy(Environment.ProcessId), TimeSpan.FromSeconds(5), NullLogger.Instance, Ct);

        public async Task<NamedPipeClientStream> ConnectRawAsync()
        {
            var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000, Ct);
            return pipe;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Outcome.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default);
            _cts.Dispose();
        }
    }

    [Fact]
    public async Task Authenticated_client_can_call_registered_operations()
    {
        await using var harness = new Harness(CreateDispatcher());
        await using var client = await harness.ConnectAsync();

        var response = await client.RequestAsync<EchoRequest, EchoResponse>("test.echo", new EchoRequest("hello ✓"), null, null, Ct);

        Assert.Equal("hello ✓", response.Text);
        Assert.Equal(Environment.ProcessId, client.ServerProcessId);
    }

    [Fact]
    public async Task Progress_messages_arrive_before_the_result()
    {
        await using var harness = new Harness(CreateDispatcher());
        await using var client = await harness.ConnectAsync();
        var seen = new List<string>();

        var response = await client.RequestAsync<EchoRequest, EchoResponse>("test.progress", new EchoRequest("step"), null,
            element => seen.Add(element.Deserialize<EchoResponse>(IpcJson.Options)!.Text), Ct);

        Assert.Equal("done", response.Text);
        Assert.Equal(["step1", "step2", "step3"], seen);
    }

    [Fact]
    public async Task Client_cancellation_reaches_the_server_handler()
    {
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new Harness(CreateDispatcher(cancelled));
        await using var client = await harness.ConnectAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequestAsync<Empty>("test.wait", null, null, cts.Token));
        Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Wrong_handshake_token_is_rejected()
    {
        await using var harness = new Harness(CreateDispatcher());

        await Assert.ThrowsAnyAsync<IpcException>(() => harness.ConnectAsync(HandshakeToken.Create()));
        Assert.Equal(IpcSessionOutcome.HandshakeFailed, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Unexpected_client_process_is_rejected_before_handshake()
    {
        await using var harness = new Harness(CreateDispatcher(), policy: new ExpectedPeerPolicy(expectedProcessId: 4));

        await Assert.ThrowsAnyAsync<Exception>(() => harness.ConnectAsync());
        Assert.Equal(IpcSessionOutcome.PeerRejected, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Client_refuses_an_unverified_server()
    {
        await using var harness = new Harness(CreateDispatcher());

        await Assert.ThrowsAsync<IpcPeerRejectedException>(() =>
            IpcClient.ConnectAsync(harness.PipeName, harness.Token, new ExpectedPeerPolicy(expectedProcessId: 4), TimeSpan.FromSeconds(5), NullLogger.Instance, Ct));
    }

    [Fact]
    public async Task Unknown_operation_is_rejected()
    {
        await using var harness = new Harness(CreateDispatcher());
        await using var client = await harness.ConnectAsync();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => client.RequestAsync<Empty>("shell.execute", null, null, Ct));
        Assert.Equal(IpcProtocol.ErrorCodes.UnsupportedOperation, error.Error.Code);

        // A tolerant server keeps the session open for valid requests.
        var ok = await client.RequestAsync<EchoRequest, EchoResponse>("test.echo", new EchoRequest("still here"), null, null, Ct);
        Assert.Equal("still here", ok.Text);
    }

    [Fact]
    public async Task Malformed_payload_is_rejected()
    {
        await using var harness = new Harness(CreateDispatcher());
        await using var client = await harness.ConnectAsync();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.RequestAsync<object, EchoResponse>("test.echo", new { text = "x", extra = "not allowed" }, null, null, Ct));
        Assert.Equal(IpcProtocol.ErrorCodes.MalformedPayload, error.Error.Code);

        var missing = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.RequestAsync<object, EchoResponse>("test.echo", new { }, null, null, Ct));
        Assert.Equal(IpcProtocol.ErrorCodes.MalformedPayload, missing.Error.Code);
    }

    [Fact]
    public async Task Strict_server_disconnects_after_a_rejected_request()
    {
        await using var harness = new Harness(CreateDispatcher(), strict: true);
        await using var client = await harness.ConnectAsync();

        await Assert.ThrowsAnyAsync<IpcException>(() => client.RequestAsync<Empty>("registry.write", null, null, Ct));
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await Assert.ThrowsAnyAsync<IpcException>(() => client.RequestAsync<EchoRequest, EchoResponse>("test.echo", new EchoRequest("x"), null, null, Ct));
    }

    [Fact]
    public async Task Requests_before_handshake_are_refused()
    {
        await using var harness = new Harness(CreateDispatcher());
        await using var pipe = await harness.ConnectRawAsync();

        await WriteJsonFrameAsync(pipe, new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Request, Guid.NewGuid(), "test.echo", null,
            JsonSerializer.SerializeToElement(new EchoRequest("sneaky"), IpcJson.Options), null));

        Assert.Equal(IpcSessionOutcome.HandshakeFailed, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Oversized_frame_ends_the_session()
    {
        await using var harness = new Harness(CreateDispatcher(), maxFrame: 4096);
        await using var client = await harness.ConnectAsync();

        await Assert.ThrowsAnyAsync<IpcException>(() =>
            client.RequestAsync<EchoRequest, EchoResponse>("test.echo", new EchoRequest(new string('x', 10_000)), null, null, Ct));
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        // Also exercise a hostile length prefix sent directly on the wire.
        await using var raw = new Harness(CreateDispatcher(), maxFrame: 4096);
        await using var pipe = await raw.ConnectRawAsync();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
        await pipe.WriteAsync(header, Ct);
        await pipe.FlushAsync(Ct);
        Assert.Equal(IpcSessionOutcome.HandshakeFailed, await raw.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Garbage_after_handshake_is_a_protocol_violation()
    {
        await using var harness = new Harness(CreateDispatcher());
        await using var pipe = await harness.ConnectRawAsync();

        await WriteJsonFrameAsync(pipe, new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Request, Guid.NewGuid(), IpcProtocol.HelloType, null,
            JsonSerializer.SerializeToElement(new HelloRequest(IpcProtocol.Version, Convert.ToBase64String(harness.Token)), IpcJson.Options), null));
        Assert.NotNull(await FrameCodec.ReadAsync(pipe, FrameCodec.DefaultMaxFrameBytes, Ct));

        await FrameCodec.WriteAsync(pipe, "{ not json"u8.ToArray(), FrameCodec.DefaultMaxFrameBytes, Ct);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Envelope_with_wrong_protocol_version_is_a_violation()
    {
        await using var harness = new Harness(CreateDispatcher());
        await using var pipe = await harness.ConnectRawAsync();

        await WriteJsonFrameAsync(pipe, new IpcEnvelope(IpcProtocol.Version, IpcProtocol.Kinds.Request, Guid.NewGuid(), IpcProtocol.HelloType, null,
            JsonSerializer.SerializeToElement(new HelloRequest(IpcProtocol.Version, Convert.ToBase64String(harness.Token)), IpcJson.Options), null));
        Assert.NotNull(await FrameCodec.ReadAsync(pipe, FrameCodec.DefaultMaxFrameBytes, Ct));

        await WriteJsonFrameAsync(pipe, new IpcEnvelope(99, IpcProtocol.Kinds.Request, Guid.NewGuid(), "test.echo", null, null, null));
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Pending_requests_fail_fast_when_the_server_goes_away()
    {
        var harness = new Harness(CreateDispatcher());
        await using var client = await harness.ConnectAsync();
        var disconnected = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnected.TrySetResult();

        var pending = client.RequestAsync<Empty>("test.wait", null, null, Ct);
        await harness.DisposeAsync();

        await Assert.ThrowsAsync<IpcDisconnectedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(client.IsConnected);
    }

    internal static Task WriteJsonFrameAsync(Stream pipe, IpcEnvelope envelope)
        => FrameCodec.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJson.Options), FrameCodec.DefaultMaxFrameBytes, Ct);
}
