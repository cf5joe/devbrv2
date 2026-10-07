using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevBR.Ipc;

public static class IpcProtocol
{
    /// <summary>Wire protocol version. Peers reject any other value.</summary>
    public const int Version = 1;

    public const string HelloType = "hello";

    public const int MaxTypeLength = 64;
    public const int TokenBytes = 32;

    public static class Kinds
    {
        public const string Request = "request";
        public const string Cancel = "cancel";
        public const string Result = "result";
        public const string Error = "error";
        public const string Progress = "progress";
    }

    public static class ErrorCodes
    {
        public const string ProtocolViolation = "protocol_violation";
        public const string Unauthorized = "unauthorized";
        public const string UnsupportedOperation = "unsupported_operation";
        public const string MalformedPayload = "malformed_payload";
        public const string Busy = "busy";
        public const string Cancelled = "cancelled";
        public const string OperationFailed = "operation_failed";
        public const string Internal = "internal_error";
    }
}

/// <param name="V">Protocol version.</param>
/// <param name="Kind">One of <see cref="IpcProtocol.Kinds"/>.</param>
/// <param name="Id">Correlates a request with its progress, result, error or cancel messages.</param>
/// <param name="Type">Operation name; required on requests.</param>
/// <param name="JobId">The job this request belongs to, when there is one.</param>
public sealed record IpcEnvelope(
    int V,
    string Kind,
    Guid Id,
    string? Type,
    Guid? JobId,
    JsonElement? Payload,
    IpcError? Error);

/// <param name="Detail">Optional machine-readable discriminator, e.g. an ArchiveErrorKind name.</param>
public sealed record IpcError(string Code, string Message, string? Detail);

public sealed record HelloRequest(int ProtocolVersion, string Token);

public sealed record HelloResponse(int ProtocolVersion, int ServerProcessId);

public sealed record Empty
{
    public static Empty Value { get; } = new();
}

public static class IpcJson
{
    /// <summary>Strict options: unknown members, missing required values and null violations are rejected.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 32,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

public class IpcException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class IpcProtocolException(string message) : IpcException(message);

/// <summary>The peer went away: the process exited, crashed, or closed the connection.</summary>
public sealed class IpcDisconnectedException(string message, Exception? inner = null) : IpcException(message, inner);

/// <summary>The peer process could not be authenticated.</summary>
public sealed class IpcPeerRejectedException(string message) : IpcException(message);

/// <summary>The remote side returned an error for a request.</summary>
public sealed class IpcRemoteException(IpcError error) : IpcException(error.Message)
{
    public IpcError Error { get; } = error;
}
