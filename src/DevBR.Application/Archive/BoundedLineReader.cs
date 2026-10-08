using System.Text;

namespace DevBR.Application.Archive;

/// <summary>
/// Reads newline-delimited records from untrusted input without ever buffering more than one bounded
/// record: an over-long line fails as soon as the limit is crossed, not after it has been read whole.
/// </summary>
public static class BoundedLineReader
{
    public static IEnumerable<string> ReadLines(string path, int maxLineChars)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);
        foreach (var line in ReadLines(reader, maxLineChars))
        {
            yield return line;
        }
    }

    /// <exception cref="InvalidDataException">A line is longer than <paramref name="maxLineChars"/>.</exception>
    public static IEnumerable<string> ReadLines(TextReader reader, int maxLineChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineChars);

        var buffer = new char[8192];
        var line = new StringBuilder();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != '\n')
                {
                    continue;
                }

                Append(line, buffer, start, i - start, maxLineChars);
                yield return Complete(line, maxLineChars);
                start = i + 1;
            }

            Append(line, buffer, start, read - start, maxLineChars);
        }

        if (line.Length > 0)
        {
            yield return Complete(line, maxLineChars);
        }
    }

    private static void Append(StringBuilder line, char[] buffer, int start, int count, int maxLineChars)
    {
        // One extra character is tolerated for a trailing '\r', which is removed when the line completes.
        if (line.Length + count > maxLineChars + 1)
        {
            throw new InvalidDataException($"A record is longer than the {maxLineChars:N0}-character limit.");
        }

        line.Append(buffer, start, count);
    }

    private static string Complete(StringBuilder line, int maxLineChars)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        if (line.Length > maxLineChars)
        {
            throw new InvalidDataException($"A record is longer than the {maxLineChars:N0}-character limit.");
        }

        var value = line.ToString();
        line.Clear();
        return value;
    }
}
