using System.Buffers.Binary;

namespace DevBR.Ipc;

/// <summary>Length-prefixed frames: a 4-byte little-endian length followed by a UTF-8 JSON envelope.</summary>
public static class FrameCodec
{
    public const int DefaultMaxFrameBytes = 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, int maxFrameBytes, CancellationToken cancellationToken)
    {
        if (payload.Length == 0 || payload.Length > maxFrameBytes)
        {
            throw new IpcProtocolException($"Outgoing frame of {payload.Length} bytes is outside the permitted range.");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <returns>The frame payload, or null when the peer closed the connection cleanly between frames.</returns>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxFrameBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            throw new IpcProtocolException("The connection closed in the middle of a frame header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > maxFrameBytes)
        {
            throw new IpcProtocolException($"Incoming frame length {length} is outside the permitted range (1..{maxFrameBytes}).");
        }

        var payload = new byte[length];
        read = await stream.ReadAtLeastAsync(payload, length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read < length)
        {
            throw new IpcProtocolException("The connection closed in the middle of a frame.");
        }

        return payload;
    }
}
