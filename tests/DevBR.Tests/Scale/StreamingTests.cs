using System.Security.Cryptography;
using DevBR.Application.Archive;
using DevBR.Application.Restore;
using DevBR.Archive;
using DevBR.Backup;
using DevBR.Discovery;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;
using DevBR.Restore;
using DevBR.Restore.Execution;
using DevBR.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Tests.Scale;

/// <summary>Heap measurements are process-wide, so these tests never run alongside others.</summary>
[CollectionDefinition(nameof(MemoryMeasurement), DisableParallelization = true)]
public sealed class MemoryMeasurement;

/// <summary>Phase 6 scale gate: large files are streamed through backup, archive and restore, never held in memory.</summary>
[Collection(nameof(MemoryMeasurement))]
public sealed class StreamingTests : IDisposable
{
    private const long LargeFile = 64L * 1024 * 1024;

    /// <summary>Well under one copy of the large file: anything that buffers it whole fails.</summary>
    private const long HeapBudget = 32L * 1024 * 1024;

    private const string DataFolder = @"C:\Data";
    private const string LargePath = @"C:\Data\large.bin";

    private readonly TempDirectory _temp = new();
    private readonly SevenZipArchiveService _archive = new(Loggers.For<SevenZipArchiveService>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Backup_and_restore_replace_a_large_file_without_buffering_it()
    {
        var source = Machine("source", seed: 1);
        var sourceHash = Hash(source.MapPath(LargePath));

        BackupResult backup;
        long backupPeak;
        using (var sampler = new PeakHeapSampler())
        {
            backup = await BackupAsync(source);
            backupPeak = sampler.PeakGrowth;
        }

        Assert.True(backup.Verified, backup.Message);
        Assert.True(backupPeak < HeapBudget, $"Backup grew the managed heap by {backupPeak:N0} bytes for a {LargeFile:N0}-byte file.");

        var overview = await new BackupReader(_archive).OpenAsync(backup.OutputPath!, null, _temp.Combine("scratch"), Ct);
        try
        {
            // The target already has a different large file at the same place; the user chose the backup's copy.
            var target = Machine("target", seed: 2);
            var previousHash = Hash(target.MapPath(LargePath));
            var writer = new SimulatedMachineWriter(target);
            var paths = new AppPaths(_temp.Combine("state"));
            paths.EnsureCreated();
            var database = new StateDatabase(paths, Loggers.For<StateDatabase>());
            await database.InitializeAsync(Ct);
            var journal = new SqliteRestoreJournal(database);
            var store = new ProtectedRollbackStore(paths.RollbackDirectory);
            var planner = new RestorePlanner(_archive, NullLogger<RestorePlanner>.Instance);
            var elevation = new SimulatedElevationProvider(target, writer, true, id => RestoreJobSummary.ApprovedEffects(journal, id));

            RestoreRequest Request(IReadOnlyDictionary<string, ConflictDecision> decisions)
                => new(overview, null, target, overview.Artifacts.Select(a => a.Record.Key).ToHashSet(), [], decisions, _temp.Combine("work"));

            var first = await planner.PreflightAsync(Request(new Dictionary<string, ConflictDecision>()), null, Ct);
            var conflict = first.Operations.Single(o => o.Operation.Target.Equals(LargePath, StringComparison.OrdinalIgnoreCase));
            var request = Request(new Dictionary<string, ConflictDecision> { [conflict.Operation.Id] = ConflictDecision.UseBackup });
            var preflight = await planner.PreflightAsync(request, null, Ct);
            Assert.Contains(preflight.Operations, o => o.Operation.Action == RestoreAction.ReplaceFile && o.Operation.Target.Equals(LargePath, StringComparison.OrdinalIgnoreCase));

            var executor = new RestoreExecutor(planner, _archive, journal, store, Loggers.For<RestoreExecutor>());
            var execution = new RestoreExecutionRequest(request, PlanApproval.Approve(preflight), writer, elevation, new SimulatedPackageInstaller(target, writer), _temp.Combine("reports"));

            RestoreRun run;
            long restorePeak;
            using (var sampler = new PeakHeapSampler())
            {
                run = await executor.ExecuteAsync(execution, null, Ct);
                restorePeak = sampler.PeakGrowth;
            }

            var replaced = run.Operations.Single(o => o.Action == RestoreAction.ReplaceFile);
            Assert.Equal(RestoreStatus.Applied, replaced.Status);
            Assert.Equal(VerificationLevel.ConfigurationApplied, replaced.Verification);
            Assert.Equal(sourceHash, Hash(target.MapPath(LargePath)));
            Assert.True(restorePeak < HeapBudget, $"Restore grew the managed heap by {restorePeak:N0} bytes for a {LargeFile:N0}-byte file.");

            // The previous large file was kept (streamed) and rollback puts it back.
            var rollback = await new RestoreRollbackService(journal, store, Loggers.For<RestoreRollbackService>()).RollbackAsync(run.JobId, target, writer, elevation, Ct);
            Assert.Empty(rollback.Failed);
            Assert.Equal(previousHash, Hash(target.MapPath(LargePath)));
        }
        finally
        {
            BackupReader.Close(overview);
        }
    }

    [Fact]
    public async Task Opening_a_backup_reads_indexes_only_and_never_payload()
    {
        var source = Machine("source", seed: 3);
        var backup = await BackupAsync(source);
        Assert.True(backup.Verified, backup.Message);

        var recording = new RecordingArchive(_archive);
        var overview = await new BackupReader(recording).OpenAsync(backup.OutputPath!, null, _temp.Combine("scratch"), Ct);
        try
        {
            Assert.NotEmpty(recording.Extracted);
            Assert.DoesNotContain(recording.Extracted, p => p.StartsWith(ArchiveContract.PayloadPrefix, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(recording.Inspections, i => i.IncludeEntries);
            Assert.True(Directory.EnumerateFiles(overview.CatalogFolder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) < 1024 * 1024);
            Assert.Equal(2, overview.Artifacts.Single().EntryCount);
        }
        finally
        {
            BackupReader.Close(overview);
        }
    }

    private SimulatedMachine Machine(string name, int seed)
    {
        var machine = new SimulatedMachineBuilder("alice", "SCALE-" + name.ToUpperInvariant())
            .File(@"C:\Data\notes.txt", "notes")
            .File(LargePath, [0])
            .Build(_temp.Combine(name));
        WriteRandom(machine.MapPath(LargePath), LargeFile, seed);
        return machine;
    }

    private async Task<BackupResult> BackupAsync(SimulatedMachine source)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new DiscoverySnapshot(Guid.NewGuid(), "scale", source.Info, true, now, now, false, DiscoveryOptions.Default, [], [], []);
        var plan = new BackupPlanner().Plan(new BackupPlanRequest(source, snapshot, [], [DataFolder], DefaultExclusions.All, [_temp.Path]), null, Ct);
        return await new BackupRunner(_archive, NullLogger<BackupRunner>.Instance).RunAsync(plan,
            new BackupRunOptions(_temp.Combine($"{Guid.NewGuid():N}.devbr"), _temp.Combine("scratch"), CompressionPreset.Store, null, false), null, Ct);
    }

    private static void WriteRandom(string path, long length, int seed)
    {
        var random = new Random(seed);
        var chunk = new byte[1 << 20];
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        for (long written = 0; written < length; written += chunk.Length)
        {
            random.NextBytes(chunk);
            output.Write(chunk, 0, (int)Math.Min(chunk.Length, length - written));
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Samples the managed heap while an operation runs. Streaming code allocates small, short-lived chunks;
    /// a whole-file buffer of the large file shows up as a jump of at least its size.
    /// </summary>
    private sealed class PeakHeapSampler : IDisposable
    {
        private readonly long _baseline;
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _peak;

        public PeakHeapSampler()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            _baseline = GC.GetTotalMemory(forceFullCollection: false);
            _peak = _baseline;
            _thread = new Thread(() =>
            {
                while (!_stop)
                {
                    Interlocked.Exchange(ref _peak, Math.Max(Interlocked.Read(ref _peak), GC.GetTotalMemory(forceFullCollection: false)));
                    Thread.Sleep(1);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public long PeakGrowth
        {
            get
            {
                Interlocked.Exchange(ref _peak, Math.Max(Interlocked.Read(ref _peak), GC.GetTotalMemory(forceFullCollection: false)));
                return Interlocked.Read(ref _peak) - _baseline;
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
        }
    }

    private sealed class RecordingArchive(IArchiveService inner) : IArchiveService
    {
        public List<string> Extracted { get; } = [];

        public List<ArchiveInspectRequest> Inspections { get; } = [];

        public Task<ArchiveCreateResult> CreateAsync(ArchiveCreateRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
            => inner.CreateAsync(request, progress, cancellationToken);

        public Task<ArchiveInspection> InspectAsync(ArchiveInspectRequest request, CancellationToken cancellationToken)
        {
            Inspections.Add(request);
            return inner.InspectAsync(request, cancellationToken);
        }

        public Task<ArchiveExtractResult> ExtractSelectedAsync(ArchiveExtractRequest request, IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken)
        {
            Extracted.AddRange(request.EntryPaths ?? ["*"]);
            return inner.ExtractSelectedAsync(request, progress, cancellationToken);
        }
    }
}
