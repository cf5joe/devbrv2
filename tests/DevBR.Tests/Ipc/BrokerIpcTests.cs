using System.Security.Principal;
using DevBR.Application.Machine;
using DevBR.Broker;
using DevBR.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Tests.Ipc;

/// <summary>The broker's real IPC configuration, attacked through the pipe.</summary>
public sealed class BrokerIpcTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedEffects(params string[] effects) : IApprovedPlanStore
    {
        public IReadOnlySet<string>? ApprovedEffects(Guid jobId, string approvalHash) => effects.ToHashSet(StringComparer.Ordinal);
    }

    private sealed class FakeEnvironment : IMachineEnvironment
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public (string Value, bool Expandable)? Read(string name) => Values.TryGetValue(name, out var v) ? (v, false) : null;

        public void Write(string name, string? value, bool expandable)
        {
            if (value is null)
            {
                Values.Remove(name);
            }
            else
            {
                Values[name] = value;
            }
        }
    }

    private sealed class Broker : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public Broker(params string[] effects)
        {
            Token = HandshakeToken.Create();
            PipeName = $"DevBR.Test.Broker.{Guid.NewGuid():N}";
            using var identity = WindowsIdentity.GetCurrent();
            var handler = new BrokerOperationHandler(new FixedEffects(effects), Environment, () => false, Loggers.For<BrokerOperationHandler>());
            var server = new IpcServer(
                BrokerServer.CreateOptions(PipeName, identity.User!, HandshakeToken.Hash(Token), new ExpectedPeerPolicy(System.Environment.ProcessId, expectedUser: identity.User)),
                handler.CreateDispatcher(),
                NullLogger.Instance);
            var listening = new TaskCompletionSource();
            server.Listening += (_, _) => listening.TrySetResult();
            Outcome = Task.Run(() => server.RunAsync(_cts.Token));
            listening.Task.Wait(TimeSpan.FromSeconds(5));
        }

        public FakeEnvironment Environment { get; } = new();

        public byte[] Token { get; }

        public string PipeName { get; }

        public Task<IpcSessionOutcome> Outcome { get; }

        public Task<IpcClient> ConnectAsync()
            => IpcClient.ConnectAsync(PipeName, Token, new ExpectedPeerPolicy(System.Environment.ProcessId), TimeSpan.FromSeconds(5), NullLogger.Instance, Ct);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Outcome.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default);
            _cts.Dispose();
        }
    }

    private static string SetEffect(string name, string value)
        => RestoreEffects.Key("SetEnvironmentVariable", $"ENV:Machine:{name}", "NoConflict", "Elevated", null, RestoreEffects.Sha256(value), "absent");

    private static Task<ApplyMachineEnvironmentResponse> ApplyAsync(IpcClient client, string name, string? expected, string value)
        => client.RequestAsync<ApplyMachineEnvironmentRequest, ApplyMachineEnvironmentResponse>(BrokerOperations.ApplyMachineEnvironment,
            new ApplyMachineEnvironmentRequest(Guid.NewGuid(), "approval", [new MachineEnvironmentChange(name, expected, value, false)]), null, null, Ct);

    private static Task<BrokerStatus> StatusAsync(IpcClient client)
        => client.RequestAsync<BrokerStatus>(BrokerOperations.Status, null, null, Ct);

    [Fact]
    public async Task An_approved_change_is_applied_and_the_session_continues()
    {
        await using var broker = new Broker(SetEffect("JAVA_HOME", @"C:\jdk"));
        await using var client = await broker.ConnectAsync();

        var response = await ApplyAsync(client, "JAVA_HOME", null, @"C:\jdk");

        Assert.Equal(["JAVA_HOME"], response.Applied);
        Assert.Equal(@"C:\jdk", broker.Environment.Values["JAVA_HOME"]);
        Assert.Equal(IpcProtocol.Version, (await StatusAsync(client)).ProtocolVersion);
    }

    [Theory]
    [InlineData("JAVA_HOME", @"C:\evil")]
    [InlineData("OTHER", @"C:\jdk")]
    public async Task An_unapproved_change_is_refused_and_ends_the_session(string name, string value)
    {
        await using var broker = new Broker(SetEffect("JAVA_HOME", @"C:\jdk"));
        await using var client = await broker.ConnectAsync();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => ApplyAsync(client, name, null, value));
        Assert.Equal(BrokerOperations.PlanNotApprovedCode, error.Error.Code);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await broker.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        // Not even the approved change is accepted afterwards.
        await Assert.ThrowsAnyAsync<IpcException>(() => ApplyAsync(client, "JAVA_HOME", null, @"C:\jdk"));
        Assert.Empty(broker.Environment.Values);
    }

    [Fact]
    public async Task An_invalid_change_ends_the_session()
    {
        await using var broker = new Broker(SetEffect("JAVA_HOME", @"C:\jdk"));
        await using var client = await broker.ConnectAsync();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => ApplyAsync(client, "A=B", null, "x"));
        Assert.Equal(BrokerOperations.InvalidChangeCode, error.Error.Code);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await broker.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_concurrent_change_is_reported_without_ending_the_session()
    {
        await using var broker = new Broker(SetEffect("JAVA_HOME", @"C:\jdk"));
        broker.Environment.Values["JAVA_HOME"] = @"C:\someone-else";
        await using var client = await broker.ConnectAsync();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => ApplyAsync(client, "JAVA_HOME", null, @"C:\jdk"));
        Assert.Equal(BrokerOperations.ConcurrentChangeCode, error.Error.Code);
        Assert.Equal(@"C:\someone-else", broker.Environment.Values["JAVA_HOME"]);
        Assert.False(broker.Outcome.IsCompleted);
        await StatusAsync(client);
    }

    [Theory]
    [InlineData("broker.runCommand")]
    [InlineData("registry.write")]
    [InlineData("hello")]
    [InlineData("BROKER.STATUS")]
    public async Task Any_operation_outside_the_allow_list_ends_the_session(string operation)
    {
        await using var broker = new Broker();
        await using var client = await broker.ConnectAsync();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => client.RequestAsync<Empty>(operation, null, null, Ct));
        Assert.Equal(IpcProtocol.ErrorCodes.UnsupportedOperation, error.Error.Code);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await broker.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task A_payload_with_unexpected_fields_ends_the_session()
    {
        await using var broker = new Broker(SetEffect("JAVA_HOME", @"C:\jdk"));
        await using var client = await broker.ConnectAsync();

        var error = await Assert.ThrowsAsync<IpcRemoteException>(() => client.RequestAsync<object, ApplyMachineEnvironmentResponse>(BrokerOperations.ApplyMachineEnvironment,
            new { jobId = Guid.NewGuid(), approvalHash = "h", changes = new[] { new { name = "JAVA_HOME", newValue = @"C:\jdk", expandable = false, force = true } } }, null, null, Ct));
        Assert.Equal(IpcProtocol.ErrorCodes.MalformedPayload, error.Error.Code);
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await broker.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Empty(broker.Environment.Values);
    }

    [Fact]
    public async Task A_frame_larger_than_the_broker_limit_ends_the_session()
    {
        await using var broker = new Broker(SetEffect("JAVA_HOME", @"C:\jdk"));
        await using var client = await broker.ConnectAsync();

        await Assert.ThrowsAnyAsync<IpcException>(() => ApplyAsync(client, "JAVA_HOME", null, new string('x', BrokerServer.MaxFrameBytes)));
        Assert.Equal(IpcSessionOutcome.ProtocolViolation, await broker.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Empty(broker.Environment.Values);
    }

    [Fact]
    public async Task A_wrong_handshake_token_never_reaches_an_operation()
    {
        await using var broker = new Broker(SetEffect("JAVA_HOME", @"C:\jdk"));

        await Assert.ThrowsAnyAsync<IpcException>(() =>
            IpcClient.ConnectAsync(broker.PipeName, HandshakeToken.Create(), new ExpectedPeerPolicy(Environment.ProcessId), TimeSpan.FromSeconds(5), NullLogger.Instance, Ct));
        Assert.Equal(IpcSessionOutcome.HandshakeFailed, await broker.Outcome.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Empty(broker.Environment.Values);
    }
}
