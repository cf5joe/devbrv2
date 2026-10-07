using System.Windows.Media;
using DevBR.Application.Settings;

namespace DevBR.App.Theming;

/// <summary>The semantic colors every view uses. Views never reference raw colors.</summary>
public sealed record Palette(
    string Name,
    bool IsDark,
    Color Background,
    Color Surface,
    Color SurfaceElevated,
    Color Border,
    Color Text,
    Color TextMuted,
    Color Accent,
    Color Success,
    Color Warning,
    Color Error)
{
    public static Palette Graphite { get; } = new(
        "Graphite", IsDark: true,
        Background: ColorMath.Parse("#18191C"),
        Surface: ColorMath.Parse("#212226"),
        SurfaceElevated: ColorMath.Parse("#2A2B30"),
        Border: ColorMath.Parse("#393A41"),
        Text: ColorMath.Parse("#F1F2F4"),
        TextMuted: ColorMath.Parse("#A9ABB3"),
        Accent: ColorMath.Parse("#4C8DFF"),
        Success: ColorMath.Parse("#4CC38A"),
        Warning: ColorMath.Parse("#F2B84B"),
        Error: ColorMath.Parse("#F26B5E"));

    public static Palette Midnight { get; } = new(
        "Midnight", IsDark: true,
        Background: ColorMath.Parse("#0B1220"),
        Surface: ColorMath.Parse("#111A2D"),
        SurfaceElevated: ColorMath.Parse("#18243B"),
        Border: ColorMath.Parse("#25334F"),
        Text: ColorMath.Parse("#E9EFF9"),
        TextMuted: ColorMath.Parse("#99A7C1"),
        Accent: ColorMath.Parse("#22C7E0"),
        Success: ColorMath.Parse("#43C99A"),
        Warning: ColorMath.Parse("#F0BC52"),
        Error: ColorMath.Parse("#F2716A"));

    public static Palette Plum { get; } = new(
        "Plum", IsDark: true,
        Background: ColorMath.Parse("#171221"),
        Surface: ColorMath.Parse("#20182D"),
        SurfaceElevated: ColorMath.Parse("#292038"),
        Border: ColorMath.Parse("#3A2D4D"),
        Text: ColorMath.Parse("#F3EEF9"),
        TextMuted: ColorMath.Parse("#B2A5C4"),
        Accent: ColorMath.Parse("#B69CFF"),
        Success: ColorMath.Parse("#56C99B"),
        Warning: ColorMath.Parse("#F0BD5A"),
        Error: ColorMath.Parse("#F47B86"));

    public static Palette Light { get; } = new(
        "Light", IsDark: false,
        Background: ColorMath.Parse("#F4F5F7"),
        Surface: ColorMath.Parse("#FFFFFF"),
        SurfaceElevated: ColorMath.Parse("#FFFFFF"),
        Border: ColorMath.Parse("#D9DBE1"),
        Text: ColorMath.Parse("#1A1B1F"),
        TextMuted: ColorMath.Parse("#5B5F6D"),
        Accent: ColorMath.Parse("#2563EB"),
        Success: ColorMath.Parse("#177A4B"),
        Warning: ColorMath.Parse("#946200"),
        Error: ColorMath.Parse("#C2372B"));

    public static Palette For(ThemePreset preset, bool windowsUsesLightTheme) => preset switch
    {
        ThemePreset.Midnight => Midnight,
        ThemePreset.Plum => Plum,
        ThemePreset.Light => Light,
        ThemePreset.FollowWindows => windowsUsesLightTheme ? Light : Graphite,
        _ => Graphite,
    };
}

public sealed record AccentChoice(string Name, string? Hex);

public static class AccentChoices
{
    /// <summary>Null hex means "use the preset's own accent".</summary>
    public static IReadOnlyList<AccentChoice> All { get; } =
    [
        new("Preset default", null),
        new("Blue", "#4C8DFF"),
        new("Cyan", "#22C7E0"),
        new("Teal", "#2BB5A0"),
        new("Green", "#4CB86A"),
        new("Amber", "#E8A33D"),
        new("Coral", "#F07560"),
        new("Pink", "#E86BAE"),
        new("Lavender", "#B69CFF"),
    ];
}
