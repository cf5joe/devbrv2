using System.Windows.Media;
using DevBR.App.Theming;
using DevBR.Application.Settings;

namespace DevBR.Tests.App;

public sealed class ThemeTests
{
    public static TheoryData<string> PaletteNames => new() { "Graphite", "Midnight", "Plum", "Light" };

    private static Palette ByName(string name) => name switch
    {
        "Graphite" => Palette.Graphite,
        "Midnight" => Palette.Midnight,
        "Plum" => Palette.Plum,
        _ => Palette.Light,
    };

    [Theory]
    [MemberData(nameof(PaletteNames))]
    public void Palette_text_meets_contrast_requirements(string name)
    {
        var palette = ByName(name);

        Assert.True(ColorMath.ContrastRatio(palette.Text, palette.Background) >= 7, "body text on background");
        Assert.True(ColorMath.ContrastRatio(palette.Text, palette.SurfaceElevated) >= 7, "body text on elevated surface");
        Assert.True(ColorMath.ContrastRatio(palette.TextMuted, palette.Surface) >= 4.5, "muted text on surface");
        Assert.True(ColorMath.ContrastRatio(palette.TextMuted, palette.SurfaceElevated) >= 4.5, "muted text on elevated surface");

        foreach (var status in new[] { palette.Success, palette.Warning, palette.Error })
        {
            Assert.True(ColorMath.ContrastRatio(status, palette.Surface) >= 3, $"status color {status} on surface");
        }
    }

    [Theory]
    [MemberData(nameof(PaletteNames))]
    public void Every_accent_choice_stays_readable_in_every_preset(string name)
    {
        var palette = ByName(name);
        foreach (var choice in AccentChoices.All)
        {
            var accent = choice.Hex is null ? palette.Accent : ColorMath.Parse(choice.Hex);
            var resources = ThemeService.Build(palette, accent);

            var foreground = ((SolidColorBrush)resources[ThemeService.Keys.AccentForeground]).Color;
            var accentText = ((SolidColorBrush)resources[ThemeService.Keys.AccentText]).Color;

            Assert.True(ColorMath.ContrastRatio(foreground, accent) >= 4.5, $"{choice.Name}: text on accent fill in {name}");
            Assert.True(ColorMath.ContrastRatio(accentText, palette.Background) >= 4.5, $"{choice.Name}: accent text on background in {name}");
            Assert.True(ColorMath.ContrastRatio(accentText, palette.Surface) >= 4.5, $"{choice.Name}: accent text on surface in {name}");
        }
    }

    [Fact]
    public void Contrast_math_matches_wcag_reference_values()
    {
        Assert.Equal(21, ColorMath.ContrastRatio(Colors.White, Colors.Black), 3);
        Assert.Equal(1, ColorMath.ContrastRatio(Colors.Gray, Colors.Gray), 3);
        Assert.Equal(4.54, ColorMath.ContrastRatio(ColorMath.Parse("#767676"), Colors.White), 2);
    }

    [Fact]
    public void Follow_windows_resolves_to_graphite_or_light()
    {
        Assert.Same(Palette.Light, Palette.For(ThemePreset.FollowWindows, windowsUsesLightTheme: true));
        Assert.Same(Palette.Graphite, Palette.For(ThemePreset.FollowWindows, windowsUsesLightTheme: false));
    }

    [Theory]
    [InlineData("#zzzzzz")]
    [InlineData("12345")]
    [InlineData("")]
    public void Invalid_accent_values_fall_back(string hex) => Assert.False(ColorMath.TryParse(hex, out _));
}
