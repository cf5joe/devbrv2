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

    /// <returns>A forward-only stream that decrypts one chunk at a time.</returns>
    public Stream Open(Guid jobId, string id)
    {
        if (id.Length != 32 || !id.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("Invalid rollback copy identifier.", nameof(id));
        }

        var path = Path.Combine(JobFolder(jobId, create: false), id + ".bin");
        return new DecryptingStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));
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

    /// <summary>Reads length-prefixed DPAPI chunks and returns their plaintext; holds at most one chunk in memory.</summary>
    private sealed class DecryptingStream(FileStream input) : Stream
    {
        private readonly byte[] _length = new byte[4];
        private byte[] _chunk = [];
        private int _offset;
        private bool _ended;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_offset == _chunk.Length)
            {
                if (_ended || !NextChunk())
                {
                    _ended = true;
                    return 0;
                }
            }

            var count = Math.Min(buffer.Length, _chunk.Length - _offset);
            _chunk.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        private bool NextChunk()
        {
            var header = ReadFull(input, _length);
            if (header == 0)
            {
                return false;
            }

            if (header != 4)
            {
                throw new InvalidDataException("The rollback copy is truncated.");
            }

            var size = BinaryPrimitives.ReadInt32LittleEndian(_length);
            if (size <= 0 || size > ChunkSize * 2)
            {
                throw new InvalidDataException("The rollback copy is damaged.");
            }

            var encrypted = new byte[size];
            if (ReadFull(input, encrypted) != size)
            {
                throw new InvalidDataException("The rollback copy is truncated.");
            }

            _chunk = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            _offset = 0;
            return true;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                input.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
