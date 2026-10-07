using System.Buffers;


namespace DevBR.Application.Archive;

/// <summary>
/// Validates entry names from untrusted archives before anything is written. Every archive is treated
/// as hostile input, including encrypted ones: hashes detect corruption, not authorship.
/// </summary>
public static class ArchivePathValidator
{
    private const int MaxEntryPathLength = 4096;
    private const int MaxSegmentLength = 255;

    private static readonly SearchValues<char> InvalidChars =
        SearchValues.Create([.. Path.GetInvalidFileNameChars(), '*', '?', '"', '<', '>', '|', ':']);

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Returns the entry path normalized to backslash separators, or throws <see cref="ArchiveException"/>.</summary>
    public static string Normalize(string entryPath)
    {
        if (!TryNormalize(entryPath, out var normalized, out var reason))
        {
            throw new ArchiveException(ArchiveErrorKind.UnsafeEntryPath, $"Unsafe archive entry path '{Printable(entryPath)}': {reason}.");
        }

        return normalized;
    }

    public static bool TryNormalize(string entryPath, out string normalized, out string reason)
    {
        normalized = string.Empty;

        if (string.IsNullOrEmpty(entryPath))
        {
            reason = "empty path";
            return false;
        }

        if (entryPath.Length > MaxEntryPathLength)
        {
            reason = "path is too long";
            return false;
        }

        var candidate = entryPath.Replace('/', '\\');

        if (candidate[0] == '\\' || Path.IsPathRooted(candidate))
        {
            reason = "absolute path";
            return false;
        }

        var segments = candidate.Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                reason = "empty path segment";
                return false;
            }

            if (segment is "." or "..")
            {
                reason = "relative traversal segment";
                return false;
            }

            if (segment.Length > MaxSegmentLength)
            {
                reason = "path segment is too long";
                return false;
            }

            // ':' covers drive letters and alternate data streams.
            if (segment.AsSpan().ContainsAny(InvalidChars) || segment.Any(char.IsControl))
            {
                reason = "invalid character";
                return false;
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                reason = "segment ends with a dot or space";
                return false;
            }

            var stem = segment.Split('.', 2)[0].TrimEnd(' ');
            if (ReservedDeviceNames.Contains(stem))
            {
                reason = "reserved device name";
                return false;
            }
        }

        normalized = string.Join('\\', segments);
        reason = string.Empty;
        return true;
    }

    /// <summary>Resolves a normalized relative path under <paramref name="root"/> and proves it stays inside.</summary>
    public static string ResolveUnder(string root, string normalizedRelativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, normalizedRelativePath));

        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveException(ArchiveErrorKind.UnsafeEntryPath, $"Archive entry '{Printable(normalizedRelativePath)}' resolves outside the destination.");
        }

        return full;
    }

    private static string Printable(string value)
    {
        var cleaned = new string([.. value.Take(200).Select(c => char.IsControl(c) ? '?' : c)]);
        return value.Length > 200 ? cleaned + "…" : cleaned;
    }
}
