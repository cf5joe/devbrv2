using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Application.Restore;
using DevBR.Archive;
using DevBR.Backup;
using DevBR.Discovery;
using DevBR.Domain;
using DevBR.Infrastructure;
using DevBR.Infrastructure.State;
using DevBR.Infrastructure.Workers;
using DevBR.Restore;
using DevBR.Restore.Execution;
using DevBR.Simulation;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevBR.Bench;

/// <summary>
/// Scale benchmark: generates a synthetic dataset on a simulated source computer, backs it up through the real
/// BackupPlanner/BackupRunner and archive worker, opens the backup, and restores it to an empty simulated target.
/// Reports wall time, throughput and peak working set (this process and the archive worker) per stage.
/// </summary>
internal static class Program
{
    private const string DataRoot = @"C:\BenchData";

    public static async Task<int> Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }

        var root = Path.GetFullPath(options.WorkRoot ?? Path.Combine(Path.GetTempPath(), "devbr-bench", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(root);
        var results = Path.GetFullPath(options.ResultsFolder ?? Path.Combine(root, "results"));
        Directory.CreateDirectory(results);
        Console.WriteLine($"DevBR bench '{options.Name}' in {root}");

        await using var host = new WorkerProcessHost(new WorkerHostOptions(), NullLoggerFactory.Instance);
        IArchiveService archive = options.InProcess
            ? new SevenZipArchiveService(NullLogger<SevenZipArchiveService>.Instance)
            : new WorkerArchiveService(host);
        using var sampler = new WorkingSetSampler(() => host.ProcessId);

        var report = new BenchReport
        {
            Name = options.Name,
            Machine = MachineSpecs.Collect(root),
            Settings = options,
            ArchiveMode = options.InProcess ? "in-process SevenZipArchiveService" : "DevBR.ArchiveWorker process",
            StartedAt = DateTimeOffset.Now,
        };

        try
        {
            // --- Dataset ---------------------------------------------------------------------------------
            var source = new SimulatedMachineBuilder("bench", "BENCH-SOURCE").Directory(DataRoot).Build(Path.Combine(root, "source"));
            var (files, bytes) = await Stage(report, sampler, "generate", () => Task.FromResult(Generate(source.MapPath(DataRoot), options)), r => r);
            Console.WriteLine($"Dataset: {files:N0} files, {Mb(bytes):N0} MB");

            // --- Backup ----------------------------------------------------------------------------------
            var now = DateTimeOffset.UtcNow;
            var snapshot = new DiscoverySnapshot(Guid.NewGuid(), "bench", source.Info, true, now, now, false, DiscoveryOptions.Default, [], [], []);
            var output = Path.Combine(root, "bench.devbr");
            var plan = await Stage(report, sampler, "plan", () => Task.FromResult(new BackupPlanner().Plan(
                new BackupPlanRequest(source, snapshot, [], [DataRoot], DefaultExclusions.All, [Path.Combine(root, "scratch"), output]), null, CancellationToken.None)),
                p => (p.Files, p.Bytes));

            var password = options.Encrypt ? new SecretText("devbr-bench-password") : null;
            var backupStages = new StageClock("backup");
            var backup = await Stage(report, sampler, "backup (total)", () => new BackupRunner(archive, NullLogger<BackupRunner>.Instance).RunAsync(plan,
                new BackupRunOptions(output, Path.Combine(root, "scratch"), options.Compression, password, false), backupStages, CancellationToken.None),
                b => (b.Files, b.Bytes));
            if (!backup.Verified)
            {
                throw new InvalidOperationException($"Backup failed: {backup.Message}");
            }

            backupStages.AddTo(report, files, bytes);
            report.ArchiveBytes = backup.ArchiveBytes;

            // --- Open (manifest overview: indexes only) -------------------------------------------------
            var overview = await Stage(report, sampler, "open backup (indexes only)", () => new BackupReader(archive).OpenAsync(output, password, Path.Combine(root, "scratch"), CancellationToken.None),
                o => (o.Artifacts.Sum(a => a.EntryCount), 0L));
            try
            {
                // --- Restore to an empty target -----------------------------------------------------------
                var target = new SimulatedMachineBuilder("bench", "BENCH-TARGET").Build(Path.Combine(root, "target"));
                var writer = new SimulatedMachineWriter(target);
                var paths = new AppPaths(Path.Combine(root, "state"));
                paths.EnsureCreated();
                var database = new StateDatabase(paths, NullLogger<StateDatabase>.Instance);
                await database.InitializeAsync(CancellationToken.None);
                var journal = new SqliteRestoreJournal(database);
                var store = new ProtectedRollbackStore(paths.RollbackDirectory);
                var planner = new RestorePlanner(archive, NullLogger<RestorePlanner>.Instance);
                var request = new RestoreRequest(overview, password, target, overview.Artifacts.Select(a => a.Record.Key).ToHashSet(), [],
                    new Dictionary<string, ConflictDecision>(), Path.Combine(root, "work"));

                var preflight = await Stage(report, sampler, "restore preflight", () => planner.PreflightAsync(request, null, CancellationToken.None),
                    p => (p.Operations.Count, 0L));
                var executor = new RestoreExecutor(planner, archive, journal, store, NullLogger<RestoreExecutor>.Instance);
                var restoreStages = new StageClock("restore");
                var elevation = new SimulatedElevationProvider(target, writer, false, _ => null);
                var run = await Stage(report, sampler, "restore execute (incl. second preflight, extraction, journal, validation)",
                    () => executor.ExecuteAsync(new RestoreExecutionRequest(request, PlanApproval.Approve(preflight), writer, elevation,
                        new SimulatedPackageInstaller(target, writer), Path.Combine(root, "reports")), restoreStages, CancellationToken.None),
                    _ => (files, bytes));
                restoreStages.AddTo(report, files, bytes);
                report.RestoreOutcome = run.Outcome.ToString();
                report.RestoreApplied = run.Operations.Count(o => o.Status == RestoreStatus.Applied);
                report.RestoreFailed = run.Operations.Count(o => o.Status is RestoreStatus.Failed or RestoreStatus.Blocked);

                // --- Check restored bytes ----------------------------------------------------------------
                report.RestoredMatches = await Stage(report, sampler, "compare restored data", () => Task.FromResult(Compare(source.MapPath(DataRoot), target.MapPath(DataRoot))), _ => (files, bytes));

                // --- Journal cost per restored file (durable intent + outcome) -----------------------------
                const int JournalOps = 1000;
                await Stage(report, sampler, $"journal only: {JournalOps:N0} intent+outcome pairs", () =>
                {
                    var job = Guid.NewGuid();
                    journal.CreateJob(job, JobKinds.Restore, "{}");
                    for (var i = 0; i < JournalOps; i++)
                    {
                        journal.RecordIntent(job, $"op{i}", i, "{\"kind\":\"file\"}");
                        journal.RecordOutcome(job, $"op{i}", "{\"state\":\"applied\"}");
                    }

                    return Task.FromResult(JournalOps);
                }, n => (n, 0L));
            }
            finally
            {
                BackupReader.Close(overview);
            }
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            Console.Error.WriteLine(ex);
        }
        finally
        {
            report.CompletedAt = DateTimeOffset.Now;
            report.PeakWorkingSetBench = Math.Max(sampler.PeakSelf, Process.GetCurrentProcess().PeakWorkingSet64);
            report.PeakWorkingSetWorker = sampler.PeakWorker;
            report.PeakManagedHeap = sampler.PeakManaged;
            Write(report, results);
            if (!options.Keep)
            {
                Console.WriteLine("Removing generated data...");
                await host.DisposeAsync();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                TryDelete(root, keep: results);
            }
        }

        return report.Error is null && report.RestoredMatches ? 0 : 1;
    }

    private static async Task<T> Stage<T>(BenchReport report, WorkingSetSampler sampler, string name, Func<Task<T>> action, Func<T, (long Items, long Bytes)> measure)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {name}...");
        sampler.StartStage();
        var clock = Stopwatch.StartNew();
        var result = await action();
        clock.Stop();
        var (items, bytes) = measure(result);
        var stage = new StageResult(name, clock.Elapsed.TotalSeconds, items, bytes, sampler.StagePeakSelf, sampler.StagePeakWorker);
        report.Stages.Add(stage);
        Console.WriteLine($"    {stage.Seconds:N1} s, {stage.FilesPerSecond:N0} files/s, {stage.MegabytesPerSecond:N1} MB/s, peak WS {Mb(stage.PeakWorkingSetBench):N0} MB (worker {Mb(stage.PeakWorkingSetWorker):N0} MB)");
        return result;
    }

    /// <summary>Many small files spread over folders of 1,000, plus large incompressible files.</summary>
    private static (long Files, long Bytes) Generate(string folder, Options options)
    {
        long files = 0, bytes = 0;
        var random = new Random(20261008);
        var buffer = new byte[Math.Max(options.SmallFileBytes, 1 << 20)];
        for (var i = 0; i < options.SmallFiles; i++)
        {
            var dir = Path.Combine(folder, "small", $"d{i / 1000:D4}");
            if (i % 1000 == 0)
            {
                Directory.CreateDirectory(dir);
            }

            // Half text-like (compressible), half random.
            var span = buffer.AsSpan(0, options.SmallFileBytes);
            if (i % 2 == 0)
            {
                random.NextBytes(span);
            }
            else
            {
                Encoding.ASCII.GetBytes($"// file {i}\n".PadRight(span.Length, 'x')).AsSpan(0, span.Length).CopyTo(span);
            }

            using (var output = new FileStream(Path.Combine(dir, $"f{i:D7}.dat"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
            {
                output.Write(span);
            }

            files++;
            bytes += span.Length;
        }

        if (options.LargeFiles > 0)
        {
            Directory.CreateDirectory(Path.Combine(folder, "large"));
        }

        for (var i = 0; i < options.LargeFiles; i++)
        {
            using var output = new FileStream(Path.Combine(folder, "large", $"blob{i:D3}.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
            for (long written = 0; written < options.LargeFileBytes; written += buffer.Length)
            {
                RandomNumberGenerator.Fill(buffer);
                output.Write(buffer, 0, (int)Math.Min(buffer.Length, options.LargeFileBytes - written));
            }

            files++;
            bytes += options.LargeFileBytes;
        }

        return (files, bytes);
    }

    private static bool Compare(string expected, string actual)
    {
        var mismatches = 0;
        foreach (var file in Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories))
        {
            var other = Path.Combine(actual, Path.GetRelativePath(expected, file));
            if (!File.Exists(other) || new FileInfo(other).Length != new FileInfo(file).Length || Hash(file) != Hash(other))
            {
                if (++mismatches <= 5)
                {
                    Console.Error.WriteLine($"Mismatch: {other}");
                }
            }
        }

        return mismatches == 0;
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Write(BenchReport report, string folder)
    {
        var stamp = report.StartedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var json = Path.Combine(folder, $"bench-{report.Name}-{stamp}.json");
        File.WriteAllText(json, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        var md = Path.Combine(folder, $"bench-{report.Name}-{stamp}.md");
        File.WriteAllText(md, report.ToMarkdown());
        Console.WriteLine();
        Console.WriteLine(report.ToMarkdown());
        Console.WriteLine($"Results: {json}");
    }

    private static void TryDelete(string root, string keep)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (string.Equals(Path.GetFullPath(entry), keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not remove {entry}: {ex.Message}");
            }
        }
    }

    internal static double Mb(long bytes) => bytes / 1048576.0;
}

internal sealed record Options
{
    public const string Usage =
        "DevBR.Bench [--name N] [--small-files COUNT] [--small-size BYTES] [--large-files COUNT] [--large-size BYTES]\n" +
        "            [--compression Store|Fast|Normal|Maximum] [--encrypt] [--in-process] [--work DIR] [--results DIR] [--keep]";

    public string Name { get; init; } = "custom";

    public int SmallFiles { get; init; }

    public int SmallFileBytes { get; init; } = 1024;

    public int LargeFiles { get; init; }

    public long LargeFileBytes { get; init; } = 1L << 30;

    public CompressionPreset Compression { get; init; } = CompressionPreset.Normal;

    public bool Encrypt { get; init; }

    public bool InProcess { get; init; }

    public string? WorkRoot { get; init; }

    public string? ResultsFolder { get; init; }

    public bool Keep { get; init; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            o = args[i] switch
            {
                "--name" => o with { Name = Next() },
                "--small-files" => o with { SmallFiles = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--small-size" => o with { SmallFileBytes = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--large-files" => o with { LargeFiles = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--large-size" => o with { LargeFileBytes = long.Parse(Next(), CultureInfo.InvariantCulture) },
                "--compression" => o with { Compression = Enum.Parse<CompressionPreset>(Next(), ignoreCase: true) },
                "--encrypt" => o with { Encrypt = true },
                "--in-process" => o with { InProcess = true },
                "--work" => o with { WorkRoot = Next() },
                "--results" => o with { ResultsFolder = Next() },
                "--keep" => o with { Keep = true },
                _ => throw new ArgumentException($"Unknown option {args[i]}."),
            };
        }

        if (o.SmallFiles + o.LargeFiles == 0)
        {
            throw new ArgumentException("Choose --small-files and/or --large-files.");
        }

        return o;
    }
}

internal sealed record StageResult(string Name, double Seconds, long Items, long Bytes, long PeakWorkingSetBench, long PeakWorkingSetWorker)
{
    public double FilesPerSecond => Seconds > 0 ? Items / Seconds : 0;

    public double MegabytesPerSecond => Seconds > 0 ? Program.Mb(Bytes) / Seconds : 0;
}

internal sealed class BenchReport
{
    public string Name { get; set; } = string.Empty;

    public MachineSpecs Machine { get; set; } = null!;

    public Options Settings { get; set; } = null!;

    public string ArchiveMode { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset CompletedAt { get; set; }

    public List<StageResult> Stages { get; } = [];

    public long ArchiveBytes { get; set; }

    public string? RestoreOutcome { get; set; }

    public int RestoreApplied { get; set; }

    public int RestoreFailed { get; set; }

    public bool RestoredMatches { get; set; }

    public long PeakWorkingSetBench { get; set; }

    public long PeakWorkingSetWorker { get; set; }

    public long PeakManagedHeap { get; set; }

    public string? Error { get; set; }

    public string ToMarkdown()
    {
        var s = new StringBuilder();
        s.AppendLine(CultureInfo.InvariantCulture, $"## {Name}");
        s.AppendLine();
        s.AppendLine(CultureInfo.InvariantCulture,
            $"{Settings.SmallFiles:N0} small files × {Settings.SmallFileBytes:N0} B, {Settings.LargeFiles} large files × {Program.Mb(Settings.LargeFileBytes):N0} MB; compression {Settings.Compression}, encrypted {Settings.Encrypt}; archive: {ArchiveMode}.");
        s.AppendLine(CultureInfo.InvariantCulture, $"Machine: {Machine.Cpu}, {Machine.LogicalProcessors} logical CPUs, {Program.Mb(Machine.RamBytes) / 1024:N1} GB RAM, disk {Machine.Disk}, {Machine.Os}, .NET {Machine.Runtime}.");
        s.AppendLine();
        s.AppendLine("| Stage | Time (s) | Items/s | MB/s | Peak WS bench (MB) | Peak WS worker (MB) |");
        s.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var stage in Stages)
        {
            s.AppendLine(CultureInfo.InvariantCulture,
                $"| {stage.Name} | {stage.Seconds:N1} | {stage.FilesPerSecond:N0} | {stage.MegabytesPerSecond:N1} | {Program.Mb(stage.PeakWorkingSetBench):N0} | {Program.Mb(stage.PeakWorkingSetWorker):N0} |");
        }

        s.AppendLine();
        s.AppendLine(CultureInfo.InvariantCulture,
            $"Archive {Program.Mb(ArchiveBytes):N0} MB. Restore: {RestoreOutcome ?? "not run"}, {RestoreApplied:N0} operations applied, {RestoreFailed:N0} failed/blocked; restored data identical: {RestoredMatches}.");
        s.AppendLine(CultureInfo.InvariantCulture,
            $"Peak working set: bench {Program.Mb(PeakWorkingSetBench):N0} MB, archive worker {Program.Mb(PeakWorkingSetWorker):N0} MB; peak managed heap {Program.Mb(PeakManagedHeap):N0} MB. Total {(CompletedAt - StartedAt).TotalMinutes:N1} min.");
        if (Error is not null)
        {
            s.AppendLine();
            s.AppendLine("**Error:** " + Error.Split('\n')[0]);
        }

        return s.ToString();
    }
}

/// <summary>Backup sub-stages, timed from the runner's own progress events.</summary>
internal sealed class StageClock(string prefix) : IProgress<OperationEvent>, IProgress<RestoreProgress>
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, (double First, double Last)> _seen = [];
    private readonly Lock _gate = new();

    public void Report(OperationEvent value) => Mark(value.Stage.ToString().ToLowerInvariant());

    public void Report(RestoreProgress value) => Mark(value.Message switch
    {
        "Checking this computer again" => "preflight again",
        "Extracting files from the backup" => "extraction",
        "Checking the restored items" => "validation",
        _ => "apply + journal",
    });

    private void Mark(string stage)
    {
        lock (_gate)
        {
            var now = _clock.Elapsed.TotalSeconds;
            _seen[stage] = _seen.TryGetValue(stage, out var span) ? (span.First, now) : (now, now);
        }
    }

    public void AddTo(BenchReport report, long files, long bytes)
    {
        var total = report.Stages[^1];
        var ordered = _seen.OrderBy(p => p.Value.First).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var start = i == 0 ? 0 : ordered[i].Value.First;
            var end = i + 1 < ordered.Count ? ordered[i + 1].Value.First : total.Seconds;
            report.Stages.Add(new StageResult($"  {prefix}: {ordered[i].Key} (approx.)", end - start, files, bytes, 0, 0));
        }
    }
}

/// <summary>Samples working sets every 200 ms: this process and, when running, the archive worker.</summary>
internal sealed class WorkingSetSampler : IDisposable
{
    private readonly Func<int?> _workerId;
    private readonly Timer _timer;
    private long _stageSelf;
    private long _stageWorker;

    public WorkingSetSampler(Func<int?> workerId)
    {
        _workerId = workerId;
        _timer = new Timer(_ => Sample(), null, 0, 200);
    }

    public long PeakSelf { get; private set; }

    public long PeakWorker { get; private set; }

    public long PeakManaged { get; private set; }

    public long StagePeakSelf
    {
        get
        {
            Sample();
            return Interlocked.Read(ref _stageSelf);
        }
    }

    public long StagePeakWorker => Interlocked.Read(ref _stageWorker);

    public void StartStage()
    {
        Interlocked.Exchange(ref _stageSelf, 0);
        Interlocked.Exchange(ref _stageWorker, 0);
        Sample();
    }

    private void Sample()
    {
        using var self = Process.GetCurrentProcess();
        var ws = self.WorkingSet64;
        Max(ref _stageSelf, ws);
        PeakSelf = Math.Max(PeakSelf, ws);
        PeakManaged = Math.Max(PeakManaged, GC.GetTotalMemory(false));
        if (_workerId() is { } id)
        {
            try
            {
                using var worker = Process.GetProcessById(id);
                var peak = worker.PeakWorkingSet64;
                Max(ref _stageWorker, worker.WorkingSet64);
                PeakWorker = Math.Max(PeakWorker, peak);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
        }
    }

    private static void Max(ref long field, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, current) != current)
        {
        }
    }

    public void Dispose() => _timer.Dispose();
}

internal sealed record MachineSpecs(string Cpu, int LogicalProcessors, long RamBytes, string Disk, string Os, string Runtime)
{
    public static MachineSpecs Collect(string folder)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var cpu = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? RuntimeInformation.ProcessArchitecture.ToString();
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var ram = GlobalMemoryStatusEx(ref memory) ? (long)memory.TotalPhys : 0;
        var drive = new DriveInfo(Path.GetPathRoot(folder)!);
        return new MachineSpecs(cpu, Environment.ProcessorCount, ram, $"work drive {drive.Name} {drive.DriveFormat}", RuntimeInformation.OSDescription, Environment.Version.ToString());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}