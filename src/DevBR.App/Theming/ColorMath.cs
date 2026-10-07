using System.Globalization;
using System.Windows.Media;

namespace DevBR.App.Theming;

/// <summary>WCAG 2.x contrast helpers used to keep any user-chosen accent readable.</summary>
public static class ColorMath
{
    public static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
    public static readonly Color Black = Color.FromRgb(0x00, 0x00, 0x00);
    public static readonly Color NearBlack = Color.FromRgb(0x10, 0x11, 0x14);

    public static Color Parse(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            throw new FormatException($"'{hex}' is not a #RRGGBB color.");
        }

        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        try
        {
            color = Parse(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// White or black, whichever reads better on <paramref name="background"/>. Pure black guarantees at
    /// least 4.58:1 for any background color.
    /// </summary>
    public static Color ForegroundFor(Color background)
        => ContrastRatio(White, background) >= ContrastRatio(Black, background) ? White : Black;

    public static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    /// <summary>
    /// Lightens or darkens <paramref name="foreground"/> just enough to reach <paramref name="minimumRatio"/>
    /// against <paramref name="background"/>, preserving its hue as far as possible.
    /// </summary>
    public static Color EnsureContrast(Color foreground, Color background, double minimumRatio)
    {
        if (ContrastRatio(foreground, background) >= minimumRatio)
        {
            return foreground;
        }

        var target = RelativeLuminance(background) < 0.5 ? White : NearBlack;
        for (var step = 1; step <= 20; step++)
        {
            var candidate = Mix(foreground, target, step * 0.05);
            if (ContrastRatio(candidate, background) >= minimumRatio)
            {
                return candidate;
            }
        }

        return target;
    }

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
