namespace DevBR.Infrastructure;

/// <summary>
/// Application state lives under %LOCALAPPDATA%\DevBR, separate from the (portable, possibly read-only)
/// program folder.
/// </summary>
public sealed class AppPaths
{
    /// <summary>
    /// Development builds only: redirects all application state (used by UI automation tests so they
    /// never touch the real %LOCALAPPDATA%\DevBR). Release builds ignore it.
    /// </summary>
    public const string DataRootVariable = "DEVBR_DATA_ROOT";

    public AppPaths(string? root = null)
    {
        Root = root ?? DevelopmentOverride() ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBR");
    }

    private static string? DevelopmentOverride()
    {
        var value = BuildInfo.IsDevelopmentBuild ? Environment.GetEnvironmentVariable(DataRootVariable) : null;
        return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
    }

    public string Root { get; }

    public string LogsDirectory => Path.Combine(Root, "logs");

    public string StateDatabasePath => Path.Combine(Root, "state", "devbr.db");

    public string SettingsPath => Path.Combine(Root, "settings.json");

    public string ReportsDirectory => Path.Combine(Root, "reports");

    public string DefaultScratchDirectory => Path.Combine(Root, "scratch");

    /// <summary>Rollback records are kept until the user explicitly removes them.</summary>
    public string RollbackDirectory => Path.Combine(Root, "rollback");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(StateDatabasePath)!);
        Directory.CreateDirectory(DefaultScratchDirectory);
        Directory.CreateDirectory(RollbackDirectory);
    }
}
