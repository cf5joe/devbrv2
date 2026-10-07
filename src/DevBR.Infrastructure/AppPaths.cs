namespace DevBR.Infrastructure;

/// <summary>
/// Application state lives under %LOCALAPPDATA%\DevBR, separate from the (portable, possibly read-only)
/// program folder.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBR");
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
