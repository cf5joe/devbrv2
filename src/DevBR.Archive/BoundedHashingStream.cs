using System.Security.Cryptography;
using DevBR.Application.Archive;

namespace DevBR.Archive;

/// <summary>
/// Write-only pass-through that hashes bytes with SHA-256, enforces the size an archive entry declared,
/// and observes cancellation on every write so large entries stop promptly.
/// </summary>
internal sealed class BoundedHashingStream(Stream inner, long maxBytes, CancellationToken cancellationToken) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesWritten { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;

    public override long Position
    {
        get => BytesWritten;
        set => throw new NotSupportedException();
    }

    public string GetHashHex() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (BytesWritten + buffer.Length > maxBytes)
        {
            throw new ArchiveException(ArchiveErrorKind.LimitExceeded, "Archive entry produced more data than its declared size.");
        }

        _hash.AppendData(buffer);
        inner.Write(buffer);
        BytesWritten += buffer.Length;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
