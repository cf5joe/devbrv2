using System.Security.Cryptography;
using DevBR.Application.Machine;

namespace DevBR.Restore.Execution;

/// <summary>Reads the current state of a restore target in the vocabulary the journal uses.</summary>
internal static class TargetProbe
{
    public static string FileState(IMachine machine, string path)
    {
        if (machine.FileSystem.DirectoryExists(path))
        {
            return "directory";
        }

        if (!machine.FileSystem.FileExists(path))
        {
            return "absent";
        }

        return HashFile(machine, path) ?? "unreadable";
    }

    public static string? HashFile(IMachine machine, string path)
    {
        try
        {
            using var stream = machine.FileSystem.OpenRead(path);
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The whole file, or null when it is larger than <paramref name="maxBytes"/> (it is never buffered beyond that).</summary>
    public static byte[]? ReadBounded(IMachine machine, string path, long maxBytes)
    {
        using var stream = machine.FileSystem.OpenRead(path);
        return ReadBounded(stream, maxBytes);
    }

    public static byte[]? ReadBoundedFile(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return ReadBounded(stream, maxBytes);
    }

    public static byte[]? ReadBounded(Stream stream, long maxBytes)
    {
        if (stream.CanSeek && stream.Length > maxBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static RegistryHive Hive(string scope) => scope == "Machine" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

    public static RegistryValue? ReadVariable(IMachine machine, string scope, string name)
        => machine.ReadEnvironment(Hive(scope)).GetValueOrDefault(name);

    public static string VariableState(IMachine machine, string scope, string name)
        => ReadVariable(machine, scope, name)?.AsString() is { } value ? Journal.Sha(value) : "absent";

    public static bool HasPathEntry(string? path, string entry)
        => path is not null && path.Split(';').Any(p => string.Equals(p.Trim(), entry, StringComparison.OrdinalIgnoreCase));

    public static bool DirectoryIsEmpty(IMachine machine, string path)
    {
        try
        {
            return !machine.FileSystem.EnumerateEntries(path).Any();
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    /// <summary>Every file below a folder, relative to it (no reparse points are followed).</summary>
    public static IEnumerable<(string Relative, FileSystemEntry Entry)> Walk(IMachine machine, string root)
    {
        var pending = new Stack<string>([string.Empty]);
        while (pending.Count > 0)
        {
            var relative = pending.Pop();
            var folder = relative.Length == 0 ? root : Path.Combine(root, relative);
            foreach (var entry in machine.FileSystem.EnumerateEntries(folder))
            {
                var child = relative.Length == 0 ? entry.Name : Path.Combine(relative, entry.Name);
                yield return (child, entry);
                if (entry.IsDirectory && !entry.IsReparsePoint)
                {
                    pending.Push(child);
                }
            }
        }
    }
}
