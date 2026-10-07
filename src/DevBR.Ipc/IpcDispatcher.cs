using System.Text.Json;

namespace DevBR.Ipc;

public sealed class IpcRequestContext(Guid requestId, Guid? jobId, string type, Func<JsonElement, Task> sendProgress)
{
    public Guid RequestId { get; } = requestId;

    public Guid? JobId { get; } = jobId;

    public string Type { get; } = type;

    public Task ReportProgressAsync<T>(T progress)
        => sendProgress(JsonSerializer.SerializeToElement(progress, IpcJson.Options));
}

/// <summary>
/// The allow-list of operations a server accepts. Each operation declares its payload type, and payloads
/// are deserialized strictly before a handler runs. Anything not registered is rejected.
/// </summary>
public sealed class IpcDispatcher
{
    private readonly Dictionary<string, Func<JsonElement?, IpcRequestContext, CancellationToken, Task<JsonElement>>> _handlers = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Operations => _handlers.Keys;

    public IpcDispatcher Register<TRequest, TResponse>(string type, Func<TRequest, IpcRequestContext, CancellationToken, Task<TResponse>> handler)
    {
        ValidateType(type);
        _handlers.Add(type, async (payload, context, cancellationToken) =>
        {
            if (payload is null || payload.Value.ValueKind != JsonValueKind.Object)
            {
                throw new IpcPayloadException("A JSON object payload is required.");
            }

            TRequest request;
            try
            {
                request = payload.Value.Deserialize<TRequest>(IpcJson.Options)
                    ?? throw new IpcPayloadException("The payload was empty.");
            }
            catch (JsonException ex)
            {
                throw new IpcPayloadException($"The payload is malformed: {ex.Message}");
            }

            var response = await handler(request, context, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(response, IpcJson.Options);
        });
        return this;
    }

    public IpcDispatcher Register<TResponse>(string type, Func<IpcRequestContext, CancellationToken, Task<TResponse>> handler)
    {
        ValidateType(type);
        _handlers.Add(type, async (payload, context, cancellationToken) =>
        {
            if (payload is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } &&
                (payload.Value.ValueKind != JsonValueKind.Object || payload.Value.EnumerateObject().Any()))
            {
                throw new IpcPayloadException("This operation does not accept a payload.");
            }

            var response = await handler(context, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(response, IpcJson.Options);
        });
        return this;
    }

    internal bool TryGetHandler(string type, out Func<JsonElement?, IpcRequestContext, CancellationToken, Task<JsonElement>> handler)
        => _handlers.TryGetValue(type, out handler!);

    private static void ValidateType(string type)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > IpcProtocol.MaxTypeLength || type == IpcProtocol.HelloType)
        {
            throw new ArgumentException($"Invalid operation type '{type}'.", nameof(type));
        }
    }
}

internal sealed class IpcPayloadException(string message) : Exception(message);
