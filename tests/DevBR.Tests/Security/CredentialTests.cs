using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DevBR.Application;
using DevBR.Application.Archive;
using DevBR.Archive;
using DevBR.Backup;
using DevBR.Discovery;
using DevBR.Infrastructure;
using DevBR.Infrastructure.Workers;
using DevBR.Tests.Backup;
using Microsoft.Extensions.Logging;

namespace DevBR.Tests.Security;

/// <summary>Records every log message, structured value and exception so tests can search them for secrets.</summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string Text => string.Join('\n', _lines);

    public int Count => _lines.Count;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public ILogger<T> For<T>() => new Logger<T>(this);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            owner._lines.Enqueue($"{category} scope {state}");
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}")) : string.Empty;
            owner._lines.Enqueue($"{category} {logLevel} {formatter(state, exception)} {values} {exception}");
        }
    }
}

/// <summary>The Phase 6 "credentials" row: passwords and secret values never leave memory except as intended.</summary>
public sealed class CredentialTests(BackupFixture fixture) : IClassFixture<BackupFixture>, IDisposable
{
    // Distinctive so any leak is unambiguous; also checked in Base64 form.
    private const string Password = "Tr0ub4dor&3-DEVBR-CANARY-PASSWORD";

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertNoPassword(string text, string where)
    {
        Assert.False(text.Contains(Password, StringComparison.Ordinal), $"The password leaked into {where}.");
        Assert.False(text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)), StringComparison.Ordinal), $"The password leaked (Base64) into {where}.");
    }

    private static void AssertNoPassword(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            AssertNoPassword(current.Message, $"the message of {current.GetType().Name}");
        }

        AssertNoPassword(error.ToString(), "the exception text");
    }

    [Fact]
    public void Requests_and_options_holding_a_password_print_it_redacted()
    {
        var secret = new SecretText(Password);
        object[] holders =
        [
            new ArchiveCreateRequest(@"C:\src", @"C:\out.devbr", secret, CompressionPreset.Fast, false),
            new ArchiveInspectRequest(@"C:\out.devbr", secret, [], 0),
            new ArchiveExtractRequest(@"C:\out.devbr", secret, @"C:\x", null),
            new BackupRunOptions(@"C:\out.devbr", @"C:\scratch", CompressionPreset.Fast, secret, false),
        ];

        foreach (var holder in holders)
        {
            AssertNoPassword(holder.ToString()!, holder.GetType().Name);
            AssertNoPassword($"{holder}", $"interpolated {holder.GetType().Name}");
            Assert.Contains("[redacted]", holder.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Archive_service_logs_and_errors_never_contain_the_password()
    {
        var logs = new CapturingLoggerFactory();
        var service = new SevenZipArchiveService(logs.For<SevenZipArchiveService>());
        _temp.WriteFile(Path.Combine("src", "manifest.json"), "{}"u8.ToArray());
        _temp.WriteFile(Path.Combine("src", "payload", "a.txt"), "data"u8.ToArray());
        var archive = _temp.Combine("enc.devbr");
        var password = new SecretText(Password);

        await service.CreateAsync(new ArchiveCreateRequest(_temp.Combine("src"), archive, password, CompressionPreset.Fast, false), null, Ct);
        await service.InspectAsync(new ArchiveInspectRequest(archive, password, ["manifest.json"], 1024), Ct);
        await service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, password, _temp.Combine("out"), null), null, Ct);

        var wrong = new SecretText(Password + "-wrong");
        var errors = new List<ArchiveException>
        {
            await Assert.ThrowsAsync<ArchiveException>(() => service.InspectAsync(new ArchiveInspectRequest(archive, wrong, [], 0), Ct)),
            await Assert.ThrowsAsync<ArchiveException>(() => service.ExtractSelectedAsync(new ArchiveExtractRequest(archive, wrong, _temp.Combine("out2"), null), null, Ct)),
        };

        Assert.All(errors, e => Assert.Equal(ArchiveErrorKind.WrongPasswordOrCorrupt, e.Kind));
        errors.ForEach(AssertNoPassword);
        Assert.True(logs.Count > 0, "the service should have logged something");
        AssertNoPassword(logs.Text, "the archive service log");

        var raw = await File.ReadAllBytesAsync(archive, Ct);
        AssertNoPassword(Encoding.UTF8.GetString(raw), "the archive bytes");
        AssertNoPassword(Encoding.Unicode.GetString(raw), "the archive bytes (UTF-16)");
    }

    [Fact]
    public async Task An_encrypted_backup_keeps_the_password_and_secret_values_out_of_logs_reports_and_errors()
    {
        var logs = new CapturingLoggerFactory();
        var archive = new SevenZipArchiveService(logs.For<SevenZipArchiveService>());
        var selected = fixture.Snapshot.Artifacts.Where(a => a.SelectedByDefault || a.Id == "environment:secret:user:GITHUB_TOKEN").ToList();
        var plan = new BackupPlanner().Plan(new BackupPlanRequest(fixture.Machine, fixture.Snapshot, selected, [], DefaultExclusions.All, [_temp.Path]), null, Ct);
        Assert.True(plan.RequiresEncryption);

        var output = _temp.Combine("secret.devbr");
        var result = await new BackupRunner(archive, logs.For<BackupRunner>()).RunAsync(plan,
            new BackupRunOptions(output, _temp.Combine("scratch"), CompressionPreset.Fast, new SecretText(Password), false), null, Ct);
        Assert.True(result.Verified);
        AssertNoPassword(result.ToString(), "the backup result");

        var overview = await new BackupReader(archive).OpenAsync(output, new SecretText(Password), _temp.Combine("scratch"), Ct);
        try
        {
            Assert.True(overview.Encrypted);
            foreach (var file in Directory.GetFiles(overview.CatalogFolder, "*", SearchOption.AllDirectories))
            {
                var text = await File.ReadAllTextAsync(file, Ct);
                AssertNoPassword(text, Path.GetFileName(file));
                Assert.DoesNotContain("ghp_SIMULATED", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            BackupReader.Close(overview);
        }

        var wrong = await Assert.ThrowsAsync<ArchiveException>(() => new BackupReader(archive).OpenAsync(output, new SecretText("not-" + Password), _temp.Combine("scratch"), Ct));
        Assert.Equal(ArchiveErrorKind.WrongPasswordOrCorrupt, wrong.Kind);
        AssertNoPassword(wrong);

        var missing = await Assert.ThrowsAsync<ArchiveException>(() => new BackupReader(archive).OpenAsync(output, null, _temp.Combine("scratch"), Ct));
        Assert.Equal(ArchiveErrorKind.PasswordRequired, missing.Kind);

        AssertNoPassword(logs.Text, "the backup log");
        Assert.DoesNotContain("ghp_SIMULATED", logs.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_worker_receives_the_password_only_over_ipc()
    {
        var started = DateTime.UtcNow.AddSeconds(-2);
        var logs = new CapturingLoggerFactory();
        _temp.WriteFile(Path.Combine("src", "manifest.json"), "{}"u8.ToArray());
        var archivePath = _temp.Combine("w.devbr");

        string commandLine;
        await using (var host = new WorkerProcessHost(new WorkerHostOptions { ExecutablePath = Path.Combine(AppContext.BaseDirectory, "DevBR.ArchiveWorker.exe") }, logs))
        {
            var service = new WorkerArchiveService(host);
            await service.CreateAsync(new ArchiveCreateRequest(_temp.Combine("src"), archivePath, new SecretText(Password), CompressionPreset.Fast, false), null, Ct);

            var wrong = await Assert.ThrowsAsync<ArchiveException>(() => service.InspectAsync(new ArchiveInspectRequest(archivePath, new SecretText(Password + "!"), [], 0), Ct));
            Assert.Equal(ArchiveErrorKind.WrongPasswordOrCorrupt, wrong.Kind);
            AssertNoPassword(wrong);

            commandLine = await CommandLineOfAsync(host.ProcessId!.Value);
        }

        Assert.Contains("--pipe", commandLine, StringComparison.Ordinal);
        AssertNoPassword(commandLine, "the worker command line");
        AssertNoPassword(logs.Text, "the GUI-side worker log");

        // The worker writes its own log; nothing it wrote during this test may contain the password.
        var logFolder = new AppPaths().LogsDirectory;
        foreach (var file in Directory.Exists(logFolder) ? Directory.GetFiles(logFolder, "worker-*.log") : [])
        {
            if (File.GetLastWriteTimeUtc(file) < started)
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                AssertNoPassword(await reader.ReadToEndAsync(Ct), Path.GetFileName(file));
            }
            catch (IOException)
            {
                // Another test's worker still holds it exclusively.
            }
        }
    }

    private static async Task<string> CommandLineOfAsync(int processId)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", $"(Get-CimInstance Win32_Process -Filter 'ProcessId={processId}').CommandLine" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.False(string.IsNullOrWhiteSpace(output), "could not read the worker command line");
        return output;
    }
}
