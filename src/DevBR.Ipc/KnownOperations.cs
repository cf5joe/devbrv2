using System.Security.Cryptography;

namespace DevBR.Ipc;

public static class WorkerOperations
{
    public const string Ping = "worker.ping";
    public const string CreateArchive = "archive.create";
    public const string InspectArchive = "archive.inspect";
    public const string ExtractArchive = "archive.extract";

    /// <summary>Error code for archive failures; <see cref="IpcError.Detail"/> carries the ArchiveErrorKind.</summary>
    public const string ArchiveErrorCode = "archive_error";
}

public sealed record WorkerStatus(int ProcessId, string Version);

/// <summary>
/// The complete set of privileged operations. The broker exposes nothing else: no shell, no arbitrary
/// registry writer, no file copy.
/// </summary>
public static class BrokerOperations
{
    public const string Status = "broker.status";
    public const string ApplyMachineEnvironment = "broker.applyMachineEnvironment";

    public const string PlanNotApprovedCode = "plan_not_approved";
    public const string InvalidChangeCode = "invalid_change";
    public const string ConcurrentChangeCode = "concurrent_change";
}

public sealed record BrokerStatus(bool IsElevated, int ProtocolVersion, IReadOnlyList<string> Operations);

/// <param name="ExpectedCurrentValue">The value preflight observed; the change is refused if the registry differs (concurrent edit).</param>
/// <param name="NewValue">Null removes the variable (only when undoing a restore that created it).</param>
/// <param name="Expandable">Preserves REG_EXPAND_SZ versus REG_SZ semantics.</param>
public sealed record MachineEnvironmentChange(string Name, string? ExpectedCurrentValue, string? NewValue, bool Expandable);

/// <param name="JobId">The restore job, whose approved effects the broker reads from the journal.</param>
/// <param name="ApprovalHash">The hash of the plan the user approved; the broker revalidates it independently.</param>
public sealed record ApplyMachineEnvironmentRequest(Guid JobId, string ApprovalHash, IReadOnlyList<MachineEnvironmentChange> Changes);

public sealed record ApplyMachineEnvironmentResponse(IReadOnlyList<string> Applied);

public static class HandshakeToken
{
    public static byte[] Create() => RandomNumberGenerator.GetBytes(IpcProtocol.TokenBytes);

    public static byte[] Hash(byte[] token) => SHA256.HashData(token);
}
