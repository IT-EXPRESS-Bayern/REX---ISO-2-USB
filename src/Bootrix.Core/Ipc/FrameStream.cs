// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Buffers.Binary;

namespace Bootrix.Core.Ipc;

/// <summary>
/// Splits a byte stream into frames: a UInt32 little-endian length followed by that many bytes.
/// Lengths are checked before anything is allocated, so a corrupt or hostile header cannot make the
/// reader reserve memory for data that never arrives.
/// </summary>
internal sealed class FrameStream(Stream stream, int maxFrameBytes = RpcConnectionOptions.DefaultMaxFrameBytes) : IDisposable
{
    public const int HeaderSize = sizeof(uint);

    private const int InitialPayloadBuffer = 64 * 1024;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[HeaderSize];

    /// <summary>Reads the next frame, or returns null when the peer closed the stream between two frames. Only one reader at a time.</summary>
    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        var got = await ReadBlockAsync(_header, cancellationToken).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < HeaderSize)
        {
            throw new RpcProtocolException("truncated frame header");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(_header);
        if (length == 0)
        {
            throw new RpcProtocolException("empty frame");
        }

        if (length > maxFrameBytes)
        {
            throw new RpcProtocolException($"frame of {length} bytes exceeds the limit of {maxFrameBytes}");
        }

        var size = (int)length;
        var buffer = new byte[Math.Min(size, InitialPayloadBuffer)];
        var filled = 0;
        while (filled < size)
        {
            if (filled == buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Min(size, buffer.Length * 2L));
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new RpcProtocolException("truncated frame");
            }

            filled += read;
        }

        return buffer;
    }

    /// <summary>
    /// Writes one frame. Only waiting for the writer lock can be cancelled: abandoning a half-written
    /// frame would leave the stream out of step, and a write that hangs is ended by closing the stream.
    /// The stream must not buffer: there is no flush, because on a pipe it does nothing but throw when
    /// the read side has just seen the end of the stream, and then fails a write that already went through.
    /// </summary>
    public async Task WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length == 0 || payload.Length > maxFrameBytes)
        {
            throw new RpcProtocolException($"outgoing message of {payload.Length} bytes is outside the limit of {maxFrameBytes}");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(HeaderSize + payload.Length);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)payload.Length);
            payload.CopyTo(buffer.AsMemory(HeaderSize));

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(buffer.AsMemory(0, HeaderSize + payload.Length), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose() => _writeLock.Dispose();

    private async ValueTask<int> ReadBlockAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
    }
}
