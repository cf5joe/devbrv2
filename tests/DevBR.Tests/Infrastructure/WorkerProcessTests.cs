using System.Diagnostics;
using DevBR.Application.Archive;
using DevBR.Infrastructure.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Tests.Infrastructure;

/// <summary>Runs the real DevBR.ArchiveWorker.exe to prove that worker failures never take the caller down.</summary>
public sealed class WorkerProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorkerProcessHost CreateHost() => new(
        new WorkerHostOptions { ExecutablePath = Path.Combine(AppContext.BaseDirectory, "DevBR.ArchiveWorker.exe") },
        NullLoggerFactory.Instance);

    [Fact]
    public async Task Worker_starts_lazily_and_answers()
    {
        await using var host = CreateHost();
        Assert.Null(host.ProcessId);

        var status = await new WorkerArchiveService(host).PingAsync(Ct);

        Assert.NotEqual(Environment.ProcessId, status.ProcessId);
        Assert.Equal(status.ProcessId, host.ProcessId);
    }

    [Fact]
    public async Task Killed_worker_is_reported_and_replaced()
    {
        await using var host = CreateHost();
        var service = new WorkerArchiveService(host);
        var faulted = new TaskCompletionSource<WorkerFaultedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Faulted += (_, e) => faulted.TrySetResult(e);

        var first = await service.PingAsync(Ct);
        using (var process = Process.GetProcessById(first.ProcessId))
        {
            process.Kill();
        }

        var fault = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(string.IsNullOrWhiteSpace(fault.Message));

        var second = await service.PingAsync(Ct);
        Assert.NotEqual(first.ProcessId, second.ProcessId);
    }

    [Fact]
    public async Task Archive_errors_cross_the_process_boundary_with_their_kind()
    {
        await using var host = CreateHost();
        var service = new WorkerArchiveService(host);

        var error = await Assert.ThrowsAsync<ArchiveException>(() =>
            service.InspectAsync(new ArchiveInspectRequest(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.devbr"), null, [], 0), Ct));

        Assert.Equal(ArchiveErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task Archive_round_trip_through_the_worker()
    {
        using var temp = new TempDirectory();
        temp.WriteFile(Path.Combine("src", "manifest.json"), "{}"u8.ToArray());
        temp.WriteFile(Path.Combine("src", "payload", "Ünïcödé.txt"), "ok"u8.ToArray());

        await using var host = CreateHost();
        var service = new WorkerArchiveService(host);
        var progressSeen = false;

        var created = await service.CreateAsync(
            new ArchiveCreateRequest(temp.Combine("src"), temp.Combine("b.devbr"), new DevBR.Application.SecretText("pw"), CompressionPreset.Fast, false),
            new Progress<ArchiveProgress>(_ => progressSeen = true), Ct);
        var inspection = await service.InspectAsync(new ArchiveInspectRequest(created.OutputPath, new DevBR.Application.SecretText("pw"), ["manifest.json"], 1024), Ct);

        Assert.True(created.Verified);
        Assert.True(inspection.Encrypted);
        Assert.Equal("{}", inspection.InlineEntries["manifest.json"]);
        Assert.Contains(inspection.Entries, e => e.Path == @"payload\Ünïcödé.txt");
        await Task.Delay(200, Ct);
        Assert.True(progressSeen);
    }

    [Fact]
    public async Task Disposing_the_host_stops_the_worker()
    {
        int pid;
        await using (var host = CreateHost())
        {
            pid = (await new WorkerArchiveService(host).PingAsync(Ct)).ProcessId;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return; // Already gone.
        }

        using (process)
        {
            Assert.True(process.WaitForExit(TimeSpan.FromSeconds(5)));
        }
    }
}
