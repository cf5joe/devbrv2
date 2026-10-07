namespace DevBR.Application.Settings;

public enum ThemePreset
{
    Graphite,
    Midnight,
    Plum,
    Light,
    FollowWindows,
}

/// <param name="AccentHex">Null uses the preset's own accent.</param>
/// <param name="ScratchDirectory">Null uses the default under the application data folder.</param>
public sealed record AppSettings(
    ThemePreset Theme,
    string? AccentHex,
    string? ScratchDirectory)
{
    public static AppSettings Default { get; } = new(ThemePreset.Graphite, null, null);
}

public interface ISettingsStore
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}
