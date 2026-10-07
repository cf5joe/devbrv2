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
}
