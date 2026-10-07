using Serilog;
using Serilog.Events;

namespace DevBR.Infrastructure.Logging;

/// <summary>
/// Structured local logs only. Retention: 30 days, and a combined cap of about 100 MB across the GUI,
/// worker and broker (each role gets a share of 10 MB files).
/// </summary>
public static class LogSetup
{
    private const long FileSizeLimitBytes = 10L * 1024 * 1024;

    public static Serilog.ILogger CreateLogger(AppPaths paths, ProcessRole role)
    {
        var retainedFiles = role switch
        {
            ProcessRole.Gui => 6,
            _ => 2,
        };

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.WithProperty("Role", role.ToString())
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, $"{role.ToString().ToLowerInvariant()}-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFiles,
                retainedFileTimeLimit: TimeSpan.FromDays(30),
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}")
            .CreateLogger();
    }
}

public enum ProcessRole
{
    Gui,
    Worker,
    Broker,
}
