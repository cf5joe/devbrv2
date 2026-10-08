using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DevBR.Ipc;

namespace DevBR.Tests.Ipc;

/// <summary>Raw-wire attacks on the IPC server: framing, handshake, envelopes and replays.</summary>
public sealed class IpcAdversarialTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IpcTests.Harness Create(bool strict = false) => new(IpcTests.CreateDispatcher(), strict: strict, maxFrame: 4096);

    private static byte[] Hello(int version, object payload, int envelopeVersion = IpcProtocol.Version)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = envelopeVersion,
            kind = IpcProtocol.Kinds.Request,
            id = Guid.NewGuid(),
            type = IpcProtocol.HelloType,
            jobId = (Guid?)null,
            payload,
            error = (object?)null,
        }, IpcJson.Options);

    private static async Task<IpcEnvelope?> ReadAsync(Stream pipe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var frame = await FrameCodec.ReadAsync(pipe, FrameCodec.DefaultMaxFrameBytes, timeout.Token);
            return frame is null ? null : JsonSerializer.Deserialize<IpcEnvelope>(frame, IpcJson.Options);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<NamedPipeClientStream> AuthenticatedRawAsync(IpcTests.Harness harness)
    {
        var pipe = await harness.ConnectRawAsync();
        await FrameCodec.WriteAsync(pipe, Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version, token = Convert.ToBase64String(harness.Token) }), 4096, Ct);
        var reply = await ReadAsync(pipe);
        Assert.Equal(IpcProtocol.Kinds.Result, reply?.Kind);
        return pipe;
    }

    private static Task SendAsync(Stream pipe, object envelope)
        => FrameCodec.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJson.Options), 4096, Ct);

    private static object Request(Guid id, string type, object? payload, string kind = IpcProtocol.Kinds.Request, int v = IpcProtocol.Version)
        => new { v, kind, id, type, jobId = (Guid?)null, payload, error = (object?)null };

    // --- Handshake ------------------------------------------------------------------------------

    public static TheoryData<string> BadHellos => new()
    {
        "wrong protocol version",
        "missing token",
        "empty payload",
        "null payload",
        "short token",
        "long token",
        "not base64",
        "unknown field",
        "token as number",
        "wrong envelope version",
        "invalid json",
        "json array",
    };

    [Theory]
    [MemberData(nameof(BadHellos))]
    public async Task Malformed_or_unauthenticated_hellos_fail_the_handshake(string variant)
    {
        await using var harness = Create();
        await using var pipe = await harness.ConnectRawAsync();
        var token = Convert.ToBase64String(harness.Token);

        var frame = variant switch
        {
            "wrong protocol version" => Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version + 1, token }),
            "missing token" => Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version }),
            "empty payload" => Hello(IpcProtocol.Version, new { }),
            "null payload" => Hello(IpcProtocol.Version, null!),
            "short token" => Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version, token = Convert.ToBase64String(harness.Token[..16]) }),
            "long token" => Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version, token = Convert.ToBase64String([.. harness.Token, 0]) }),
            "not base64" => Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version, token = "!!!" + token }),
            "unknown field" => Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version, token, admin = true }),
            "token as number" => Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version, token = 12345 }),
            "wrong envelope version" => Hello(IpcProtocol.Version, new { protocolVersion = IpcProtocol.Version, token }, envelopeVersion: 0),
            "invalid json" => Encoding.UTF8.GetBytes("{\"v\":1,\"kind\":\"request\",\"type\":\"hello\",\"payload\":{\"token\":\"" + token + "\""),
            _ => "[1,2,3]"u8.ToArray(),
        };

        await FrameCodec.WriteAsync(pipe, frame, 4096, Ct);

        Assert.Equal(IpcSessionOutcome.HandshakeFailed, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        var reply = await ReadAsync(pipe);
        Assert.NotEqual(IpcProtocol.Kinds.Result, reply?.Kind);
        if (reply?.Error is { } error)
        {
            Assert.DoesNotContain(token, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_client_that_never_says_hello_is_dropped()
    {
        await using var harness = Create();
        await using var pipe = await harness.ConnectRawAsync();

        Assert.Equal(IpcSessionOutcome.HandshakeFailed, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task A_second_hello_after_the_handshake_is_not_an_operation()
    {
        await using var harness = Create(strict: true);
        await using var pipe = await AuthenticatedRawAsync(harness);

        await SendAsync(pipe, Request(Guid.NewGuid(), IpcProtocol.HelloType, new { protocolVersion = IpcProtocol.Version, token = Convert.ToBase64String(harness.Token) }));

        Assert.Equal(IpcProtocol.ErrorCodes.UnsupportedOperation, (await ReadAsync(pipe))?.Error?.Code);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    // --- Framing --------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(4097)]
    public async Task Frame_lengths_outside_the_permitted_range_end_the_session(int length)
    {
        await using var harness = Create();
        await using var pipe = await AuthenticatedRawAsync(harness);

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await pipe.WriteAsync(header, Ct);
        await pipe.FlushAsync(Ct);

        Assert.Equal(IpcProtocol.ErrorCodes.ProtocolViolation, (await ReadAsync(pipe))?.Error?.Code);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_frame_truncated_mid_body_ends_the_session()
    {
        await using var harness = Create();
        var pipe = await AuthenticatedRawAsync(harness);

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 100);
        await pipe.WriteAsync(header, Ct);
        await pipe.WriteAsync("{\"v\":1"u8.ToArray(), Ct);
        await pipe.FlushAsync(Ct);
        await pipe.DisposeAsync();

        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_frame_truncated_mid_header_ends_the_session()
    {
        await using var harness = Create();
        var pipe = await AuthenticatedRawAsync(harness);

        await pipe.WriteAsync(new byte[] { 0x10, 0x00 }, Ct);
        await pipe.FlushAsync(Ct);
        await pipe.DisposeAsync();

        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Frame_codec_rejects_out_of_range_frames_in_both_directions()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<IpcProtocolException>(() => FrameCodec.WriteAsync(stream, ReadOnlyMemory<byte>.Empty, 16, Ct));
        await Assert.ThrowsAsync<IpcProtocolException>(() => FrameCodec.WriteAsync(stream, new byte[17], 16, Ct));
        Assert.Equal(0, stream.Length);

        // A header that promises a huge frame must fail before any buffer of that size is allocated.
        var hostile = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3]);
        var before = GC.GetAllocatedBytesForCurrentThread();
        await Assert.ThrowsAsync<IpcProtocolException>(() => FrameCodec.ReadAsync(hostile, 1024, Ct));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);

        Assert.Null(await FrameCodec.ReadAsync(new MemoryStream(), 1024, Ct));
        await Assert.ThrowsAsync<IpcProtocolException>(() => FrameCodec.ReadAsync(new MemoryStream([5, 0, 0, 0, 1, 2]), 1024, Ct));
    }

    // --- Envelopes ------------------------------------------------------------------------------

    public static TheoryData<string> BadEnvelopes => new()
    {
        "empty id",
        "client sends a result",
        "client sends progress",
        "client sends an error",
        "unknown kind",
        "missing kind",
        "future version",
        "not an object",
        "unknown envelope field",
    };

    [Theory]
    [MemberData(nameof(BadEnvelopes))]
    public async Task Malformed_envelopes_after_the_handshake_end_the_session(string variant)
    {
        await using var harness = Create();
        await using var pipe = await AuthenticatedRawAsync(harness);
        var echo = new { text = "x" };

        object envelope = variant switch
        {
            "empty id" => Request(Guid.Empty, "test.echo", echo),
            "client sends a result" => Request(Guid.NewGuid(), "test.echo", echo, IpcProtocol.Kinds.Result),
            "client sends progress" => Request(Guid.NewGuid(), "test.echo", echo, IpcProtocol.Kinds.Progress),
            "client sends an error" => Request(Guid.NewGuid(), "test.echo", echo, IpcProtocol.Kinds.Error),
            "unknown kind" => Request(Guid.NewGuid(), "test.echo", echo, "execute"),
            "missing kind" => new { v = 1, id = Guid.NewGuid(), type = "test.echo", jobId = (Guid?)null, payload = echo, error = (object?)null },
            "future version" => Request(Guid.NewGuid(), "test.echo", echo, v: 2),
            "not an object" => "just a string",
            _ => new { v = 1, kind = IpcProtocol.Kinds.Request, id = Guid.NewGuid(), type = "test.echo", jobId = (Guid?)null, payload = echo, error = (object?)null, elevate = true },
        };
        await SendAsync(pipe, envelope);

        Assert.Equal(IpcProtocol.ErrorCodes.ProtocolViolation, (await ReadAsync(pipe))?.Error?.Code);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("TEST.ECHO")]
    [InlineData("test.echo ")]
    [InlineData("test.echo\u0000")]
    [InlineData("test.echo/../test.wait")]
    public async Task Operation_names_must_match_the_allow_list_exactly(string type)
    {
        await using var harness = Create();
        await using var pipe = await AuthenticatedRawAsync(harness);

        await SendAsync(pipe, Request(Guid.NewGuid(), type, new { text = "x" }));
        Assert.Equal(IpcProtocol.ErrorCodes.UnsupportedOperation, (await ReadAsync(pipe))?.Error?.Code);
    }

    [Fact]
    public async Task An_overlong_operation_name_is_rejected()
    {
        await using var harness = Create();
        await using var pipe = await AuthenticatedRawAsync(harness);

        await SendAsync(pipe, Request(Guid.NewGuid(), new string('a', IpcProtocol.MaxTypeLength + 1), null));
        Assert.Equal(IpcProtocol.ErrorCodes.UnsupportedOperation, (await ReadAsync(pipe))?.Error?.Code);
    }

    [Fact]
    public async Task Replaying_an_in_flight_request_id_is_a_violation()
    {
        await using var harness = Create(strict: true);
        await using var pipe = await AuthenticatedRawAsync(harness);
        var id = Guid.NewGuid();

        await SendAsync(pipe, Request(id, "test.wait", null));
        await SendAsync(pipe, Request(id, "test.wait", null));

        var reply = await ReadAsync(pipe);
        Assert.Equal(id, reply?.Id);
        Assert.Equal(IpcProtocol.ErrorCodes.ProtocolViolation, reply?.Error?.Code);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await harness.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Cancelling_an_unknown_request_changes_nothing()
    {
        await using var harness = Create();
        await using var pipe = await AuthenticatedRawAsync(harness);

        await SendAsync(pipe, Request(Guid.NewGuid(), "test.echo", null, IpcProtocol.Kinds.Cancel));
        var id = Guid.NewGuid();
        await SendAsync(pipe, Request(id, "test.echo", new { text = "still here" }));

        var reply = await ReadAsync(pipe);
        Assert.Equal(id, reply?.Id);
        Assert.Equal(IpcProtocol.Kinds.Result, reply?.Kind);
    }

    [Fact]
    public async Task Floods_beyond_the_concurrency_limit_are_refused_as_busy()
    {
        await using var harness = Create();
        await using var pipe = await AuthenticatedRawAsync(harness);

        for (var i = 0; i < 5; i++)
        {
            await SendAsync(pipe, Request(Guid.NewGuid(), "test.wait", null));
        }

        Assert.Equal(IpcProtocol.ErrorCodes.Busy, (await ReadAsync(pipe))?.Error?.Code);
        Assert.False(harness.Outcome.IsCompleted);
    }
}
