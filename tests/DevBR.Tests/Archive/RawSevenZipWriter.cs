using System.Text;

namespace DevBR.Tests.Archive;

/// <summary>One entry of a hand-built 7z archive. Every field is written verbatim, so tests can lie about sizes or attributes.</summary>
public sealed record RawEntry(string Name, byte[] Data)
{
    /// <summary>The unpacked size recorded in the header; defaults to the real length.</summary>
    public ulong? DeclaredSize { get; init; }

    public uint? Attributes { get; init; }

    public bool WithCrc { get; init; } = true;
}

/// <summary>
/// Writes a minimal 7z archive (one Copy-coded folder per entry, plain header) without going through
/// any library that might sanitize names, sizes or attributes. Used to simulate hostile archives.
/// </summary>
public static class RawSevenZipWriter
{
    public const uint ReparsePointAttribute = 0x400;

    /// <summary>The p7zip convention: high 16 bits hold a Unix mode when 0x8000 is set.</summary>
    public const uint UnixSymlinkAttributes = 0x8000 | (0xA1FFu << 16);

    public static void Write(string path, params RawEntry[] entries)
    {
        var packed = new MemoryStream();
        foreach (var entry in entries)
        {
            packed.Write(entry.Data);
        }

        var header = new List<byte> { 0x01, 0x04 };

        // PackInfo
        header.Add(0x06);
        WriteNumber(header, 0);
        WriteNumber(header, (ulong)entries.Length);
        header.Add(0x09);
        foreach (var entry in entries)
        {
            WriteNumber(header, (ulong)entry.Data.Length);
        }

        header.Add(0x00);

        // UnpackInfo: one Copy coder per folder.
        header.Add(0x07);
        header.Add(0x0B);
        WriteNumber(header, (ulong)entries.Length);
        header.Add(0x00);
        foreach (var _ in entries)
        {
            WriteNumber(header, 1);
            header.Add(0x01);
            header.Add(0x00);
        }

        header.Add(0x0C);
        foreach (var entry in entries)
        {
            WriteNumber(header, entry.DeclaredSize ?? (ulong)entry.Data.Length);
        }

        header.Add(0x00);

        // SubStreamsInfo: one stream per folder, with its CRC.
        if (entries.All(e => e.WithCrc))
        {
            header.Add(0x08);
            header.Add(0x0A);
            header.Add(0x01);
            foreach (var entry in entries)
            {
                WriteUInt32(header, Crc32(entry.Data));
            }

            header.Add(0x00);
        }

        header.Add(0x00);

        // FilesInfo
        header.Add(0x05);
        WriteNumber(header, (ulong)entries.Length);

        var names = new List<byte> { 0x00 };
        foreach (var entry in entries)
        {
            names.AddRange(Encoding.Unicode.GetBytes(entry.Name));
            names.Add(0);
            names.Add(0);
        }

        header.Add(0x11);
        WriteNumber(header, (ulong)names.Count);
        header.AddRange(names);

        if (entries.Any(e => e.Attributes is not null))
        {
            var attributes = new List<byte> { 0x01, 0x00 };
            foreach (var entry in entries)
            {
                WriteUInt32(attributes, entry.Attributes ?? 0x20);
            }

            header.Add(0x15);
            WriteNumber(header, (ulong)attributes.Count);
            header.AddRange(attributes);
        }

        header.Add(0x00);
        header.Add(0x00);

        var headerBytes = header.ToArray();
        var startHeader = new byte[20];
        BitConverter.TryWriteBytes(startHeader.AsSpan(0, 8), (ulong)packed.Length);
        BitConverter.TryWriteBytes(startHeader.AsSpan(8, 8), (ulong)headerBytes.Length);
        BitConverter.TryWriteBytes(startHeader.AsSpan(16, 4), Crc32(headerBytes));

        using var file = File.Create(path);
        file.Write([(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]);
        file.Write(BitConverter.GetBytes(Crc32(startHeader)));
        file.Write(startHeader);
        packed.Position = 0;
        packed.CopyTo(file);
        file.Write(headerBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static void WriteUInt32(List<byte> output, uint value) => output.AddRange(BitConverter.GetBytes(value));

    /// <summary>7z variable-length NUMBER: leading one-bits in the first byte count the extra bytes.</summary>
    private static void WriteNumber(List<byte> output, ulong value)
    {
        for (var extra = 0; extra < 8; extra++)
        {
            if (value < 1UL << (7 * (extra + 1)))
            {
                var mask = (byte)(0xFF << (8 - extra));
                output.Add((byte)(mask | (byte)(value >> (8 * extra))));
                for (var i = 0; i < extra; i++)
                {
                    output.Add((byte)(value >> (8 * i)));
                }

                return;
            }
        }

        output.Add(0xFF);
        output.AddRange(BitConverter.GetBytes(value));
    }
}
