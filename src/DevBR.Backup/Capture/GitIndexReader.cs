using System.Buffers.Binary;
using System.Text;

namespace DevBR.Backup.Capture;

/// <summary>
/// Reads the paths tracked in a Git index file (versions 2–4) without running git. Used so exclusion
/// rules never silently drop tracked content.
/// </summary>
public static class GitIndexReader
{
    private const int MaxEntries = 5_000_000;

    /// <returns>Tracked paths with forward slashes, or null when the index is missing or unreadable.</returns>
    public static HashSet<string>? ReadTrackedPaths(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    public static HashSet<string>? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("DIRC"u8))
        {
            return null;
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        if (version is < 2 or > 4 || count > MaxEntries)
        {
            return null;
        }

        var paths = new HashSet<string>((int)count, StringComparer.OrdinalIgnoreCase);
        var offset = 12;
        var previous = string.Empty;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var entryStart = offset;
                offset += 60; // ctime, mtime, dev, ino, mode, uid, gid, size, 20-byte object id
                var flags = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                offset += 2;
                if (version >= 3 && (flags & 0x4000) != 0)
                {
                    offset += 2; // extended flags
                }

                string path;
                if (version == 4)
                {
                    // Prefix compression: strip N bytes from the previous path, then append a NUL-terminated suffix.
                    var strip = ReadVarint(data, ref offset);
                    var end = data[offset..].IndexOf((byte)0);
                    var suffix = Encoding.UTF8.GetString(data.Slice(offset, end));
                    offset += end + 1;
                    var prefixLength = Encoding.UTF8.GetByteCount(previous) - (int)strip;
                    var prefixBytes = Encoding.UTF8.GetBytes(previous)[..prefixLength];
                    path = Encoding.UTF8.GetString(prefixBytes) + suffix;
                }
                else
                {
                    var end = data[offset..].IndexOf((byte)0);
                    path = Encoding.UTF8.GetString(data.Slice(offset, end));
                    offset += end;

                    // Entries are NUL-padded to a multiple of eight bytes (always at least one NUL).
                    var length = offset - entryStart;
                    offset = entryStart + ((length + 8) & ~7);
                }

                paths.Add(path);
                previous = path;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return null;
        }

        return paths;
    }

    /// <summary>True when any tracked path lies inside <paramref name="relativeDirectory"/> (forward or back slashes).</summary>
    public static bool ContainsTrackedContent(HashSet<string> tracked, string relativeDirectory)
    {
        var prefix = relativeDirectory.Replace('\\', '/').TrimEnd('/') + "/";
        return tracked.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static long ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        var b = data[offset++];
        long value = b & 0x7F;
        while ((b & 0x80) != 0)
        {
            b = data[offset++];
            value = ((value + 1) << 7) | (long)(b & 0x7F);
        }

        return value;
    }
}
