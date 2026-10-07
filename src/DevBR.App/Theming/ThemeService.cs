using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using DevBR.Application.Settings;
using Microsoft.Win32;

namespace DevBR.App.Theming;

/// <summary>
/// Applies the selected preset and accent as semantic brush resources, keeps WPF's Fluent theme in the
/// matching light/dark mode, and defers to Windows high-contrast colors whenever that mode is on.
/// </summary>
public sealed class ThemeService
{
    /// <summary>Semantic resource keys. Views reference only these (via DynamicResource).</summary>
    public static class Keys
    {
        public const string Background = "DevBR.Background";
        public const string Surface = "DevBR.Surface";
        public const string SurfaceElevated = "DevBR.SurfaceElevated";
        public const string Border = "DevBR.Border";
        public const string Text = "DevBR.Text";
        public const string TextMuted = "DevBR.TextMuted";
        public const string Accent = "DevBR.Accent";
        public const string AccentForeground = "DevBR.AccentForeground";
        public const string AccentText = "DevBR.AccentText";
        public const string AccentSubtle = "DevBR.AccentSubtle";
        public const string Success = "DevBR.Success";
        public const string Warning = "DevBR.Warning";
        public const string Error = "DevBR.Error";
        public const string SuccessSubtle = "DevBR.SuccessSubtle";
        public const string WarningSubtle = "DevBR.WarningSubtle";
        public const string ErrorSubtle = "DevBR.ErrorSubtle";
    }

    private readonly ISettingsStore _settings;
    private ResourceDictionary? _applied;

    public ThemeService(ISettingsStore settings)
    {
        _settings = settings;
    }

    public event EventHandler? ThemeChanged;

    public Palette? CurrentPalette { get; private set; }

    public bool IsHighContrast => SystemParameters.HighContrast;

    /// <summary>Transitions run only when Windows animations are on and high contrast is off.</summary>
    public bool AnimationsEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public void Initialize()
    {
        Apply(_settings.Current);
        _settings.Changed += (_, settings) => Dispatch(() => Apply(settings));
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public void Apply(AppSettings settings)
    {
        var app = System.Windows.Application.Current;
        ResourceDictionary resources;

        if (SystemParameters.HighContrast)
        {
            app.ThemeMode = ThemeMode.System;
            CurrentPalette = null;
            resources = BuildHighContrast();
        }
        else
        {
            var palette = Palette.For(settings.Theme, WindowsUsesLightTheme());
            var accent = ColorMath.TryParse(settings.AccentHex, out var chosen) ? chosen : palette.Accent;
            app.ThemeMode = palette.IsDark ? ThemeMode.Dark : ThemeMode.Light;
            CurrentPalette = palette;
            resources = Build(palette, accent);
        }

        // Changing ThemeMode reloads the Fluent dictionaries; keep ours last so its keys win.
        var merged = app.Resources.MergedDictionaries;
        if (_applied is not null)
        {
            merged.Remove(_applied);
        }

        merged.Add(resources);
        _applied = resources;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public static ResourceDictionary Build(Palette palette, Color accent)
    {
        // Fill uses the accent as chosen; its foreground is whichever of white/black reads best.
        // Accent-colored text is nudged until it reaches 4.5:1 on both background and surface.
        var accentText = ColorMath.EnsureContrast(ColorMath.EnsureContrast(accent, palette.Background, 4.5), palette.Surface, 4.5);
        var accentForeground = ColorMath.ForegroundFor(accent);

        var dictionary = new ResourceDictionary();
        void Brush(string key, Color color) => dictionary[key] = Frozen(new SolidColorBrush(color));

        Brush(Keys.Background, palette.Background);
        Brush(Keys.Surface, palette.Surface);
        Brush(Keys.SurfaceElevated, palette.SurfaceElevated);
        Brush(Keys.Border, palette.Border);
        Brush(Keys.Text, palette.Text);
        Brush(Keys.TextMuted, palette.TextMuted);
        Brush(Keys.Accent, accent);
        Brush(Keys.AccentForeground, accentForeground);
        Brush(Keys.AccentText, accentText);
        Brush(Keys.AccentSubtle, ColorMath.WithAlpha(accent, palette.IsDark ? (byte)0x33 : (byte)0x22));
        Brush(Keys.Success, palette.Success);
        Brush(Keys.Warning, palette.Warning);
        Brush(Keys.Error, palette.Error);
        Brush(Keys.SuccessSubtle, ColorMath.WithAlpha(palette.Success, 0x26));
        Brush(Keys.WarningSubtle, ColorMath.WithAlpha(palette.Warning, 0x26));
        Brush(Keys.ErrorSubtle, ColorMath.WithAlpha(palette.Error, 0x26));

        // Feed the accent into WPF's Fluent control styles too.
        var hover = ColorMath.Mix(accent, palette.IsDark ? ColorMath.White : ColorMath.NearBlack, 0.12);
        var pressed = ColorMath.Mix(accent, palette.IsDark ? ColorMath.NearBlack : ColorMath.White, 0.15);
        dictionary["SystemAccentColor"] = accent;
        dictionary["SystemAccentColorLight1"] = hover;
        dictionary["SystemAccentColorLight2"] = ColorMath.Mix(accent, ColorMath.White, 0.25);
        dictionary["SystemAccentColorLight3"] = ColorMath.Mix(accent, ColorMath.White, 0.4);
        dictionary["SystemAccentColorDark1"] = pressed;
        dictionary["SystemAccentColorDark2"] = ColorMath.Mix(accent, ColorMath.NearBlack, 0.3);
        dictionary["SystemAccentColorDark3"] = ColorMath.Mix(accent, ColorMath.NearBlack, 0.45);
        Brush("AccentFillColorDefaultBrush", accent);
        Brush("AccentFillColorSecondaryBrush", hover);
        Brush("AccentFillColorTertiaryBrush", pressed);
        Brush("AccentTextFillColorPrimaryBrush", accentText);
        Brush("AccentTextFillColorSecondaryBrush", accentText);
        Brush("TextOnAccentFillColorPrimaryBrush", accentForeground);
        Brush("TextOnAccentFillColorSecondaryBrush", accentForeground);

        return dictionary;
    }

    private static ResourceDictionary BuildHighContrast()
    {
        var dictionary = new ResourceDictionary();
        void Brush(string key, Color color) => dictionary[key] = Frozen(new SolidColorBrush(color));

        Brush(Keys.Background, SystemColors.WindowColor);
        Brush(Keys.Surface, SystemColors.WindowColor);
        Brush(Keys.SurfaceElevated, SystemColors.WindowColor);
        Brush(Keys.Border, SystemColors.WindowTextColor);
        Brush(Keys.Text, SystemColors.WindowTextColor);
        Brush(Keys.TextMuted, SystemColors.WindowTextColor);
        Brush(Keys.Accent, SystemColors.HighlightColor);
        Brush(Keys.AccentForeground, SystemColors.HighlightTextColor);
        Brush(Keys.AccentText, SystemColors.HotTrackColor);
        Brush(Keys.AccentSubtle, SystemColors.HighlightColor);
        Brush(Keys.Success, SystemColors.WindowTextColor);
        Brush(Keys.Warning, SystemColors.WindowTextColor);
        Brush(Keys.Error, SystemColors.WindowTextColor);
        Brush(Keys.SuccessSubtle, SystemColors.WindowColor);
        Brush(Keys.WarningSubtle, SystemColors.WindowColor);
        Brush(Keys.ErrorSubtle, SystemColors.WindowColor);
        return dictionary;
    }

    public static bool WindowsUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
        {
            Dispatch(() => Apply(_settings.Current));
        }
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            Dispatch(() => Apply(_settings.Current));
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
