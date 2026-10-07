using System.Buffers.Binary;
using System.Text;

namespace DevBR.Simulation;

/// <summary>Writes a minimal version-2 Git index listing tracked paths, for simulated repositories.</summary>
public static class GitIndexWriter
{
    public static byte[] Create(IEnumerable<string> trackedPaths)
    {
        var paths = trackedPaths.Select(p => p.Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        using var stream = new MemoryStream();
        Span<byte> word = stackalloc byte[4];

        stream.Write("DIRC"u8);
        BinaryPrimitives.WriteUInt32BigEndian(word, 2);
        stream.Write(word);
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)paths.Count);
        stream.Write(word);

        foreach (var path in paths)
        {
            var name = Encoding.UTF8.GetBytes(path);
            var entry = new byte[(62 + name.Length + 8) & ~7];
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(24), 0x81A4); // mode 100644
            BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(60), (ushort)Math.Min(name.Length, 0xFFF));
            name.CopyTo(entry, 62);
            stream.Write(entry);
        }

        stream.Write(new byte[20]); // trailing checksum (not validated by DevBR)
        return stream.ToArray();
    }
}
