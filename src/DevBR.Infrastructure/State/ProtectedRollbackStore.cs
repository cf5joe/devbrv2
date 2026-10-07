using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DevBR.Application.Restore;

namespace DevBR.Infrastructure.State;

/// <summary>
/// Rollback copies, encrypted with DPAPI for the current user (they can hold the user's settings and,
/// when credentials were restored, secrets) in a folder only the user can open. Content is protected in
/// 1 MB chunks so large files never have to fit in memory.
/// </summary>
public sealed class ProtectedRollbackStore(string root) : IRollbackStore
{
    private const int ChunkSize = 1 << 20;
    private static readonly byte[] Entropy = "DevBR rollback v1"u8.ToArray();

    public string Save(Guid jobId, Stream content)
    {
        var folder = JobFolder(jobId, create: true);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(folder, id + ".bin");
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
        {
            var buffer = new byte[ChunkSize];
            Span<byte> length = stackalloc byte[4];
            int read;
            while ((read = ReadFull(content, buffer)) > 0)
            {
                var protectedChunk = ProtectedData.Protect(buffer.AsSpan(0, read).ToArray(), Entropy, DataProtectionScope.CurrentUser);
                BinaryPrimitives.WriteInt32LittleEndian(length, protectedChunk.Length);
                output.Write(length);
                output.Write(protectedChunk);
            }
        }

        return id;
    }

    public Stream Open(Guid jobId, string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("Invalid rollback copy identifier.", nameof(id));
        }

        var path = Path.Combine(JobFolder(jobId, create: false), id + ".bin");
        var result = new MemoryStream();
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var length = new byte[4];
            while (ReadFull(input, length) == 4)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (size <= 0 || size > ChunkSize * 2)
                {
                    throw new InvalidDataException("The rollback copy is damaged.");
                }

                var chunk = new byte[size];
                if (ReadFull(input, chunk) != size)
                {
                    throw new InvalidDataException("The rollback copy is truncated.");
                }

                result.Write(ProtectedData.Unprotect(chunk, Entropy, DataProtectionScope.CurrentUser));
            }
        }

        result.Position = 0;
        return result;
    }

    public void DeleteJob(Guid jobId)
    {
        var folder = Path.Combine(root, jobId.ToString("N"));
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private string JobFolder(Guid jobId, bool create)
    {
        var folder = Path.Combine(root, jobId.ToString("N"));
        if (create && !Directory.Exists(folder))
        {
            Directory.CreateDirectory(root);
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.CreateDirectory(folder);
        }

        return folder;
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total;
    }
}
