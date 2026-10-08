using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevBR.Application.Archive;
using DevBR.Application.Machine;
using DevBR.Backup.Capture;
using DevBR.Discovery;
using DevBR.Discovery.Support;
using DevBR.Domain;
using Microsoft.Extensions.Logging;

namespace DevBR.Backup;

/// <summary>
/// Executes an approved <see cref="BackupPlan"/>: copies the selected bytes into a private staging folder
/// while hashing them, writes the indexes, and has the archive worker compress and re-verify the result
/// before it is given its final name. Staging is always removed; a cancelled or failed backup leaves no output.
/// </summary>
public sealed class BackupRunner(IArchiveService archive, ILogger<BackupRunner> logger)
{
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);
    private const long SecretScanLimit = 1024 * 1024;
    private const long Fat32MaxFile = (4L * 1024 * 1024 * 1024) - 1;

    public static JsonSerializerOptions IndexJson { get; } = CreateIndexOptions();

    public const string Disclosure =
        "Custom files and Git history can contain secrets that automatic detection misses. An unencrypted backup should be stored and shared as carefully as the original files.";

    public async Task<BackupResult> RunAsync(BackupPlan plan, BackupRunOptions options, IProgress<OperationEvent>? progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var started = DateTimeOffset.UtcNow;
        var archiveId = Guid.NewGuid();
        var output = Path.GetFullPath(options.OutputPath);
        var encrypted = options.Password is not null;

        BackupResult Fail(BackupOutcome outcome, ArchiveErrorKind? kind, string message, IReadOnlyList<ArtifactCaptureResult>? artifacts = null, IReadOnlyList<string>? warnings = null)
            => new(outcome, null, 0, 0, 0, false, encrypted, artifacts ?? [], warnings ?? [], kind, message, clock.Elapsed);

        // --- Preconditions: nothing is written until these hold. ---------------------------------------
        if (File.Exists(output) && !options.AllowOverwrite)
        {
            return Fail(BackupOutcome.Failed, ArchiveErrorKind.OutputExists, $"{output} already exists. Choose another name or confirm that it may be replaced.");
        }

        if (plan.RequiresEncryption && !encrypted)
        {
            return Fail(BackupOutcome.EncryptionRequired, null, "This backup must be encrypted: " + string.Join(" ", plan.EncryptionReasons));
        }

        var outputDirectory = Path.GetDirectoryName(output);
        if (outputDirectory is null || !Directory.Exists(outputDirectory))
        {
            return Fail(BackupOutcome.Failed, ArchiveErrorKind.NotFound, "The output folder does not exist.");
        }

        var outputDrive = new DriveInfo(Path.GetPathRoot(output)!);
        if (outputDrive.IsReady && string.Equals(outputDrive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase) && plan.EstimatedArchiveUpperBound > Fat32MaxFile)
        {
            return Fail(BackupOutcome.Failed, ArchiveErrorKind.InvalidRequest,
                $"{outputDrive.Name} uses FAT32, which cannot hold files larger than 4 GB, and this backup may be up to {Size(plan.EstimatedArchiveUpperBound)}. Choose an NTFS or exFAT drive.");
        }

        Directory.CreateDirectory(options.ScratchRoot);
        var scratchDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.ScratchRoot))!);
        var stagingNeed = plan.Bytes + (64L * 1024 * 1024);
        var outputNeed = plan.EstimatedArchiveUpperBound;
        if (string.Equals(scratchDrive.Name, outputDrive.Name, StringComparison.OrdinalIgnoreCase)
            ? scratchDrive.AvailableFreeSpace < stagingNeed + outputNeed
            : scratchDrive.AvailableFreeSpace < stagingNeed || outputDrive.AvailableFreeSpace < outputNeed)
        {
            return Fail(BackupOutcome.Failed, ArchiveErrorKind.InsufficientSpace,
                $"Not enough free space: staging needs about {Size(stagingNeed)} on {scratchDrive.Name} and the backup up to {Size(outputNeed)} on {outputDrive.Name}.");
        }

        var staging = Path.Combine(Path.GetFullPath(options.ScratchRoot), $"backup-{archiveId:N}");
        var results = new List<ArtifactCaptureResult>();
        var warnings = new List<string>();

        try
        {
            CreatePrivateDirectory(staging);

            // --- Staging ---------------------------------------------------------------------------------
            using var stage = new StagingContext(plan, staging, encrypted, progress, archiveId, cancellationToken);
            foreach (var artifact in plan.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(artifact switch
                {
                    { Blocked: true } => new ArtifactCaptureResult(artifact.Artifact.Id, artifact.Artifact.DisplayName, "Blocked", 0, 0,
                        [.. plan.Findings.Where(f => f.ArtifactId == artifact.Artifact.Id && f.Level == FindingLevel.Blocking).Select(f => f.Message)]),
                    { Kind: CaptureKind.InventoryOnly } => new ArtifactCaptureResult(artifact.Artifact.Id, artifact.Artifact.DisplayName, "Complete", 0, 0, []),
                    { Kind: CaptureKind.Environment } => StageEnvironment(stage, artifact),
                    _ => StageFiles(stage, artifact),
                });
            }

            warnings.AddRange(results.SelectMany(r => r.Warnings.Select(w => $"{r.DisplayName}: {w}")));

            if (!encrypted && stage.SecretDetections.Count > 0)
            {
                return Fail(BackupOutcome.EncryptionRequired, null,
                    $"Recognized secrets were found in {stage.SecretDetections.Count} selected file(s), for example {string.Join(", ", stage.SecretDetections.Take(3))}. Encrypt the backup or deselect those items.",
                    results, warnings);
            }

            // --- Indexes ---------------------------------------------------------------------------------
            var completed = DateTimeOffset.UtcNow;
            WriteIndexes(plan, stage, results, warnings, archiveId, started, completed, encrypted);

            // --- Compression and verification (archive worker) -----------------------------------------
            var totalBytes = stage.TotalBytes;
            var archiveProgress = progress is null ? null : new SynchronousProgress<ArchiveProgress>(p =>
                progress.Report(new OperationEvent(archiveId, p.Stage == "Verifying" ? OperationStage.Verification : OperationStage.Compression, p.CurrentItem,
                    0, null, p.Percent is { } percent ? totalBytes * percent / 100 : 0, totalBytes, p.Stage == "Verifying" ? "Verifying the backup" : "Compressing",
                    EventSeverity.Information, DateTimeOffset.UtcNow, clock.Elapsed, null, EtaConfidence.None)));

            var created = await archive.CreateAsync(
                new ArchiveCreateRequest(staging, output, options.Password, options.Compression, options.AllowOverwrite, ArchiveContract.EntriesIndexPath),
                archiveProgress, cancellationToken).ConfigureAwait(false);

            var outcome = results.Any(r => r.Status != "Complete") || warnings.Count > 0 ? BackupOutcome.SucceededWithWarnings : BackupOutcome.Succeeded;
            logger.LogInformation("Backup {ArchiveId} written: {Files} files, {Bytes} bytes, outcome {Outcome}.", archiveId, created.FileCount, created.UncompressedBytes, outcome);

            return new BackupResult(outcome, created.OutputPath, created.ArchiveBytes, stage.FileCount, totalBytes,
                created.Verified && created.VerifiedPayloadFiles == stage.FileCount,
                encrypted, results, warnings, null,
                outcome == BackupOutcome.Succeeded ? "The backup was created and verified." : "The backup was created and verified, with warnings about some items.",
                clock.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return Fail(BackupOutcome.Cancelled, null, "The backup was cancelled. No backup file was written.", results, warnings);
        }
        catch (ArchiveException ex)
        {
            logger.LogWarning(ex, "Backup {ArchiveId} failed in the archive stage.", archiveId);
            return Fail(BackupOutcome.Failed, ex.Kind, ex.Message, results, warnings);
        }
        catch (IOException ex) when (ex.HResult is ErrorDiskFull or ErrorHandleDiskFull)
        {
            return Fail(BackupOutcome.Failed, ArchiveErrorKind.InsufficientSpace, "The disk ran out of space while preparing the backup. No backup file was written.", results, warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Backup {ArchiveId} failed while staging.", archiveId);
            return Fail(BackupOutcome.Failed, ArchiveErrorKind.IoFailure, $"The backup could not be prepared: {ex.Message}", results, warnings);
        }
        finally
        {
            DeleteStaging(staging);
        }
    }

    private ArtifactCaptureResult StageFiles(StagingContext stage, PlannedArtifact artifact)
    {
        var warnings = new List<string>();
        var status = "Complete";
        long files = 0, bytes = 0;
        var fs = stage.Plan.Machine.FileSystem;

        // Repositories must be stable for the whole capture: compare Git metadata before and after.
        var gitStamp = artifact.Artifact.Kind == ArtifactKind.Repository ? GitStamp(fs, artifact) : null;

        foreach (var file in artifact.Files)
        {
            stage.CancellationToken.ThrowIfCancellationRequested();
            var relative = file.RelativePath.Length == 0 ? Path.GetFileName(file.SourcePath) : file.RelativePath;
            var archivePath = $"payload/{artifact.Key}/r{file.RootIndex}/{relative.Replace('\\', '/')}";
            var destination = Path.Combine(stage.Root, archivePath.Replace('/', '\\'));

            if (file.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                stage.AddEntry(new ArchiveEntry(artifact.Artifact.Id, file.RelativePath, ArchiveEntryType.Directory, 0, file.LastWriteUtc, null, FileAttributes.Directory, null, archivePath));
                continue;
            }

            try
            {
                var before = fs.GetEntry(file.SourcePath);
                if (before is null)
                {
                    warnings.Add($"{file.SourcePath} was removed before it could be captured.");
                    status = "Incomplete";
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var (size, hash, omitted) = CopyFile(stage, artifact, file.SourcePath, destination);
                if (omitted is not null)
                {
                    warnings.Add(omitted);
                    status = "Incomplete";
                    continue;
                }

                var after = fs.GetEntry(file.SourcePath);
                if (after is null || after.Length != before.Length || after.LastWriteUtc != before.LastWriteUtc)
                {
                    warnings.Add($"{file.SourcePath} changed while it was being captured.");
                    status = "Inconsistent";
                }

                stage.AddEntry(new ArchiveEntry(artifact.Artifact.Id, file.RelativePath, ArchiveEntryType.File, size, before.LastWriteUtc, hash, FileAttributes.Normal, null, archivePath));
                files++;
                bytes += size;
                stage.Advance(size, file.SourcePath);
            }
            catch (IOException ex) when (ex.HResult is ErrorSharingViolation or ErrorLockViolation)
            {
                warnings.Add($"{file.SourcePath} is in use by another program and was not captured. Close the program and back up again.");
                status = "Incomplete";
            }
            catch (UnauthorizedAccessException)
            {
                warnings.Add($"{file.SourcePath} could not be read (access denied).");
                status = "Incomplete";
            }
            catch (FileNotFoundException)
            {
                warnings.Add($"{file.SourcePath} was removed while the backup was running.");
                status = "Incomplete";
            }
        }

        if (gitStamp is not null && GitStamp(fs, artifact) != gitStamp)
        {
            // Never label a live, changing repository as a verified capture.
            warnings.Add("The repository changed while it was being captured; this copy may be inconsistent. Close tools using it and back up again.");
            status = "Inconsistent";
        }

        return new ArtifactCaptureResult(artifact.Artifact.Id, artifact.Artifact.DisplayName, status, files, bytes, warnings);
    }

    /// <returns>Captured size and hash, or a reason the file was omitted.</returns>
    private static (long Size, string? Hash, string? Omitted) CopyFile(StagingContext stage, PlannedArtifact artifact, string source, string destination)
    {
        var fs = stage.Plan.Machine.FileSystem;
        var transform = CaptureTransforms.For(artifact.Artifact.Id, Path.GetFileName(source));

        using var input = fs.OpenRead(source);
        if (transform is not null && input.CanSeek && input.Length > CaptureTransforms.MaxBytes)
        {
            return (0, null, $"{source} is too large to redact safely and was left out rather than copied with its secrets.");
        }

        var small = input.CanSeek && input.Length <= Math.Max(SecretScanLimit, transform is null ? 0 : CaptureTransforms.MaxBytes);

        if (transform is not null || (small && !stage.Encrypted))
        {
            // Never more than the redaction or secret-scan limit in memory, even when the source cannot report its length.
            if (ReadBounded(input, transform is null ? SecretScanLimit : CaptureTransforms.MaxBytes) is not { } bytes)
            {
                return (0, null, transform is null
                    ? $"{source} grew while it was being captured and was left out. Close the program using it and back up again."
                    : $"{source} is too large to redact safely and was left out rather than copied with its secrets.");
            }

            if (transform is not null)
            {
                if (transform(bytes) is not { } transformed)
                {
                    return (0, null, $"{source} could not be parsed for safe redaction and was left out rather than copied with its secrets.");
                }

                bytes = transformed;
            }
            else if (artifact.Artifact.Sensitivity != Sensitivity.Credential && LooksLikeTextWithSecret(bytes))
            {
                stage.SecretDetections.Add(source);
            }

            File.WriteAllBytes(destination, bytes);
            return (bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), null);
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        var chunk = new byte[1 << 16];
        long total = 0;
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            stage.CancellationToken.ThrowIfCancellationRequested();
            sha.AppendData(chunk, 0, read);
            output.Write(chunk, 0, read);
            total += read;
        }

        return (total, Convert.ToHexStringLower(sha.GetHashAndReset()), null);
    }

    private static byte[]? ReadBounded(Stream input, long maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1 << 16];
        int read;
        while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool LooksLikeTextWithSecret(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
        {
            return false; // binary
        }

        return SecretDetector.LooksLikeSecretValue(Encoding.UTF8.GetString(bytes));
    }

    private static ArtifactCaptureResult StageEnvironment(StagingContext stage, PlannedArtifact artifact)
    {
        var machine = stage.Plan.Machine;
        var id = artifact.Artifact.Id;
        var variables = new List<CapturedVariable>();

        void Add(RegistryHive hive, Func<string, RegistryValue, bool> include)
        {
            foreach (var (name, value) in machine.ReadEnvironment(hive).OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (include(name, value))
                {
                    variables.Add(new CapturedVariable(name, hive == RegistryHive.CurrentUser ? "User" : "Machine", value.Kind, value.AsString() ?? string.Empty));
                }
            }
        }

        if (id == "environment:machine")
        {
            Add(RegistryHive.LocalMachine, (n, v) => !SecretDetector.IsSecret(n, v.AsString()));
        }
        else if (id == "environment:user")
        {
            Add(RegistryHive.CurrentUser, (n, v) => !SecretDetector.IsSecret(n, v.AsString()));
        }
        else if (id.Split(':') is ["environment", "secret", var scope, .. var rest])
        {
            var name = string.Join(':', rest);
            Add(scope == "machine" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, (n, _) => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        var archivePath = $"payload/{artifact.Key}/environment.json";
        var destination = Path.Combine(stage.Root, archivePath.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(variables, IndexJson);
        File.WriteAllBytes(destination, bytes);
        stage.AddEntry(new ArchiveEntry(id, "environment.json", ArchiveEntryType.File, bytes.Length, DateTimeOffset.UtcNow,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), FileAttributes.Normal, null, archivePath));
        stage.Advance(bytes.Length, artifact.Artifact.DisplayName);

        return new ArtifactCaptureResult(id, artifact.Artifact.DisplayName, "Complete", 1, bytes.Length, []);
    }

    private static void WriteIndexes(BackupPlan plan, StagingContext stage, List<ArtifactCaptureResult> results, List<string> warnings,
        Guid archiveId, DateTimeOffset started, DateTimeOffset completed, bool encrypted)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        void WriteLines<T>(string relative, IEnumerable<T> records)
        {
            var path = Path.Combine(stage.Root, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                foreach (var record in records)
                {
                    writer.Write(JsonSerializer.Serialize(record, IndexJson));
                    writer.Write('\n');
                }
            }

            hashes[relative] = Hash(path);
        }

        void WriteJson<T>(string relative, T value)
        {
            var path = Path.Combine(stage.Root, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, IndexJson));
            hashes[relative] = Hash(path);
        }

        hashes[ArchiveContract.EntriesIndexPath] = Hash(stage.CompleteEntries());
        WriteLines(ArchiveContract.ArtifactsIndexPath, plan.Artifacts.Select(a =>
        {
            var result = results.First(r => r.ArtifactId == a.Artifact.Id);
            return new ArtifactIndexRecord(a.Key, a.Artifact, a.Kind, a.Roots, result.Status, result.Files, result.Bytes, result.Warnings);
        }));

        var withInventory = plan.Artifacts.Any(a => a.Artifact.Id == DiscoveryEngine.InventoryArtifactId);
        WriteLines(ArchiveContract.InventoryIndexPath, withInventory
            ? plan.Snapshot.Items.Where(i => i.Category != InventoryCategory.EnvironmentVariable).Select(Sanitize)
            : []);
        WriteJson(ArchiveContract.CoveragePath, withInventory ? plan.Snapshot.Coverage : []);

        var report = new BackupReport(archiveId, started, completed, encrypted, results, plan.Findings, warnings,
            [.. plan.Artifacts.SelectMany(a => a.Exclusions)], Disclosure);
        WriteJson(ArchiveContract.BackupReportPath, report);

        var manifest = new BackupManifest(
            new ArchiveFormatVersion(ArchiveContract.CurrentMajor, ArchiveContract.CurrentMinor),
            AppVersion(),
            archiveId,
            plan.Machine.Info.ComputerName,
            $"{plan.Machine.Info.OsProductName} {plan.Machine.Info.OsDisplayVersion} (build {plan.Machine.Info.OsBuild})".Replace("  ", " ", StringComparison.Ordinal),
            plan.Machine.Info.Architecture,
            completed,
            encrypted,
            new BackupTotals(plan.Artifacts.Count, stage.EntryCount, stage.TotalBytes),
            hashes,
            [.. warnings.Take(200)]);
        File.WriteAllText(Path.Combine(stage.Root, ArchiveContract.ManifestPath), BackupManifestReader.Serialize(manifest), new UTF8Encoding(false));
    }

    /// <summary>Inventory records never carry raw environment values into the archive.</summary>
    private static InventoryItem Sanitize(InventoryItem item)
        => item.Properties?.ContainsKey("value") == true
            ? item with { Properties = item.Properties.Where(p => p.Key != "value").ToDictionary(p => p.Key, p => p.Value) }
            : item;

    private static string? GitStamp(IMachineFileSystem fs, PlannedArtifact artifact)
    {
        var root = artifact.Roots.FirstOrDefault()?.SourcePath;
        if (root is null)
        {
            return null;
        }

        var gitDir = Paths.Combine(root, ".git");
        if (!fs.DirectoryExists(gitDir))
        {
            gitDir = root; // bare repository or external git directory
        }

        var parts = new[] { "HEAD", "index", "packed-refs", "FETCH_HEAD", "ORIG_HEAD" }
            .Select(name => fs.GetEntry(Paths.Combine(gitDir, name)))
            .Select(e => e is null ? "-" : $"{e.Length}:{e.LastWriteUtc.UtcTicks}");
        return string.Join('|', parts);
    }

    private static void CreatePrivateDirectory(string path)
    {
        // Only the current user may read staged copies of configuration and repositories.
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.CreateDirectory(path);
    }

    private void DeleteStaging(string staging)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(staging); attempt++)
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    logger.LogWarning(ex, "Could not remove staging folder {Staging}.", staging);
                }

                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string AppVersion()
        => (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(BackupRunner).Assembly)
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0.0} MB";

    private static JsonSerializerOptions CreateIndexOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class StagingContext(BackupPlan plan, string root, bool encrypted, IProgress<OperationEvent>? progress, Guid jobId, CancellationToken cancellationToken) : IDisposable
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Stopwatch _throttle = Stopwatch.StartNew();
        private long _files;
        private long _bytes;

        public BackupPlan Plan { get; } = plan;

        public string Root { get; } = root;

        public bool Encrypted { get; } = encrypted;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        /// <summary>Entries go straight to entries.ndjson as they are captured, so memory does not grow with the file count.</summary>
        private StreamWriter? _entries;

        public long EntryCount { get; private set; }

        public long FileCount { get; private set; }

        public long TotalBytes { get; private set; }

        public void AddEntry(ArchiveEntry entry)
        {
            if (_entries is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EntriesPath)!);
                _entries = new StreamWriter(new FileStream(EntriesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), new UTF8Encoding(false));
            }

            _entries.Write(JsonSerializer.Serialize(entry, IndexJson));
            _entries.Write('\n');
            EntryCount++;
            TotalBytes += entry.Size;
            if (entry.EntryType == ArchiveEntryType.File)
            {
                FileCount++;
            }
        }

        /// <summary>Closes entries.ndjson (creating it empty if nothing was captured) and returns its path.</summary>
        public string CompleteEntries()
        {
            if (_entries is not null)
            {
                _entries.Dispose();
                _entries = null;
            }
            else if (!File.Exists(EntriesPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EntriesPath)!);
                File.WriteAllBytes(EntriesPath, []);
            }

            return EntriesPath;
        }

        private string EntriesPath => Path.Combine(Root, ArchiveContract.EntriesIndexPath.Replace('/', '\\'));

        public void Dispose() => _entries?.Dispose();

        public List<string> SecretDetections { get; } = [];

        public void Advance(long bytes, string item)
        {
            _files++;
            _bytes += bytes;
            if (progress is null || _throttle.ElapsedMilliseconds < 100)
            {
                return;
            }

            _throttle.Restart();

            // ETA only from measured throughput, and only once there is enough of it to mean something.
            TimeSpan? eta = null;
            var confidence = EtaConfidence.None;
            var elapsed = _clock.Elapsed;
            if (elapsed > TimeSpan.FromSeconds(3) && _bytes > 0 && Plan.Bytes > _bytes)
            {
                eta = TimeSpan.FromSeconds((Plan.Bytes - _bytes) / (_bytes / elapsed.TotalSeconds));
                confidence = elapsed > TimeSpan.FromSeconds(15) ? EtaConfidence.High : EtaConfidence.Low;
            }

            progress.Report(new OperationEvent(jobId, OperationStage.Staging, item, _files, Plan.Files, _bytes, Plan.Bytes,
                string.Create(CultureInfo.InvariantCulture, $"Copying {Path.GetFileName(item)}"), EventSeverity.Information, DateTimeOffset.UtcNow, elapsed, eta, confidence));
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
