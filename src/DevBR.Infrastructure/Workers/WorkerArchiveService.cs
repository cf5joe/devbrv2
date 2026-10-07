using System.Text.Json;
using DevBR.Application.Archive;
using DevBR.Ipc;

namespace DevBR.Infrastructure.Workers;

/// <summary>GUI-side <see cref="IArchiveService"/> that forwards every call to the archive worker process.</summary>
public sealed class WorkerArchiveService(WorkerProcessHost host) : IArchiveService
{
    public Task<ArchiveCreateResult> CreateAsync(ArchiveCreateRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
        => CallAsync<ArchiveCreateRequest, ArchiveCreateResult>(WorkerOperations.CreateArchive, request, progress, cancellationToken);

    public Task<ArchiveInspection> InspectAsync(ArchiveInspectRequest request, CancellationToken cancellationToken)
        => CallAsync<ArchiveInspectRequest, ArchiveInspection>(WorkerOperations.InspectArchive, request, null, cancellationToken);

    public Task<ArchiveExtractResult> ExtractSelectedAsync(ArchiveExtractRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
        => CallAsync<ArchiveExtractRequest, ArchiveExtractResult>(WorkerOperations.ExtractArchive, request, progress, cancellationToken);

    public async Task<WorkerStatus> PingAsync(CancellationToken cancellationToken)
    {
        var client = await host.GetClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.RequestAsync<WorkerStatus>(WorkerOperations.Ping, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> CallAsync<TRequest, TResponse>(string operation, TRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var client = await host.GetClientAsync(cancellationToken).ConfigureAwait(false);
            Action<JsonElement>? onProgress = progress is null
                ? null
                : element =>
                {
                    if (element.Deserialize<ArchiveProgress>(IpcJson.Options) is { } value)
                    {
                        progress.Report(value);
                    }
                };

            return await client.RequestAsync<TRequest, TResponse>(operation, request, null, onProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (IpcRemoteException ex) when (ex.Error.Code == WorkerOperations.ArchiveErrorCode)
        {
            var kind = Enum.TryParse<ArchiveErrorKind>(ex.Error.Detail, out var parsed) ? parsed : ArchiveErrorKind.Internal;
            throw new ArchiveException(kind, ex.Error.Message, ex);
        }
        catch (IpcRemoteException ex) when (ex.Error.Code == IpcProtocol.ErrorCodes.Cancelled)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (Exception ex) when (ex is IpcDisconnectedException or IpcPeerRejectedException or FileNotFoundException or TimeoutException)
        {
            throw new ArchiveException(ArchiveErrorKind.WorkerUnavailable, "The background archive process is unavailable. The operation did not complete; you can retry it.", ex);
        }
    }
}
