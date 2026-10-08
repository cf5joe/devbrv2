using DevBR.Application.Archive;
using DevBR.Archive;
using DevBR.Infrastructure.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Tests.Scale;

/// <summary>Restores of large folders name more entries than fit in one IPC frame; the worker spools them through files.</summary>
public sealed class WorkerSpoolTests : IDisposable
{
    private const int Files = 8000;

    private readonly TempDirectory _temp = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Extracting_more_entries_than_one_frame_holds_goes_through_the_worker()
    {
        var source = _temp.Combine("src");
        var folder = "payload/a0001/r0/" + new string('d', 60);
        var names = Enumerable.Range(0, Files).Select(i => $"{folder}/file-{i:D5}-{new string('n', 60)}.txt").ToList();
        foreach (var name in names)
        {
            _temp.WriteFile(Path.Combine("src", name.Replace('/', '\\')), [(byte)'x']);
        }

        var archivePath = _temp.Combine("many.devbr");
        await new SevenZipArchiveService(Loggers.For<SevenZipArchiveService>())
            .CreateAsync(new ArchiveCreateRequest(source, archivePath, null, CompressionPreset.Store, false), null, Ct);

        // Well over the 1 MB frame limit if sent inline.
        Assert.True(names.Sum(n => n.Length + 3) > 1024 * 1024);

        await using var host = new WorkerProcessHost(
            new WorkerHostOptions { ExecutablePath = Path.Combine(AppContext.BaseDirectory, "DevBR.ArchiveWorker.exe") }, NullLoggerFactory.Instance);
        var service = new WorkerArchiveService(host);

        var selected = await service.ExtractSelectedAsync(new ArchiveExtractRequest(archivePath, null, _temp.Combine("selected"), names), null, Ct);
        Assert.Equal(Files, selected.Files.Count);
        Assert.All(selected.Files, f => Assert.True(File.Exists(f.DestinationPath)));
        Assert.Equal(Files, selected.Bytes);

        var everything = await service.ExtractSelectedAsync(new ArchiveExtractRequest(archivePath, null, _temp.Combine("all"), null), null, Ct);
        Assert.Equal(Files, everything.Files.Count);

        // Spool folders are removed.
        Assert.Empty(Directory.EnumerateDirectories(_temp.Path, "*.spool-*"));
    }
}
