using System.Globalization;

namespace DevBR.App.Services;

public static class Formatting
{
    private static readonly string[] Units = ["bytes", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes == 1 ? "1 byte" : $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString(value < 10 ? "0.0" : "0", CultureInfo.CurrentCulture)} {Units[unit]}";
    }

    public static string Count(long count, string singular, string plural)
        => $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? singular : plural)}";

    /// <summary>Elapsed time as m:ss, or h:mm:ss past an hour.</summary>
    public static string Elapsed(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";

    /// <summary>A rounded time-left phrase: "less than a minute left", "about 4 min left", "about 1 h 20 min left".</summary>
    public static string TimeLeft(TimeSpan remaining) => remaining switch
    {
        { TotalSeconds: < 60 } => "less than a minute left",
        { TotalMinutes: < 60 } => $"about {Math.Ceiling(remaining.TotalMinutes).ToString("0", CultureInfo.CurrentCulture)} min left",
        _ => $"about {(int)remaining.TotalHours} h {remaining.Minutes} min left",
    };
}
