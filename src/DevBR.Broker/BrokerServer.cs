using System.Security.Principal;
using DevBR.Ipc;

namespace DevBR.Broker;

/// <summary>The broker's IPC hardening, shared by the executable and its tests.</summary>
public static class BrokerServer
{
    public const int MaxFrameBytes = 64 * 1024;

    public static IpcServerOptions CreateOptions(string pipeName, SecurityIdentifier clientSid, byte[] tokenSha256, IPeerPolicy peerPolicy) => new()
    {
        PipeName = pipeName,
        Security = PipeSecurityFactory.Create(clientSid),
        ExpectedTokenSha256 = tokenSha256,
        PeerPolicy = peerPolicy,
        MaxFrameBytes = MaxFrameBytes,
        MaxConcurrentRequests = 1,
        DisconnectOnRejectedRequest = true,
        IsRejection = BrokerOperationHandler.IsRejection,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        ErrorMapper = BrokerOperationHandler.MapError,
    };
}
