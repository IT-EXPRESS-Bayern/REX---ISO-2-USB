// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>Positioned reads and writes on the stream that holds the file system.</summary>
internal sealed class ExtDevice
{
    private const int ZeroChunk = 1 << 20;

    private readonly Stream _stream;

    public ExtDevice(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable, writable and seekable.", nameof(stream));
        }

        _stream = stream;
    }

    public long Length => _stream.Length;

    /// <summary>Bytes past the end of the stream read as zero, like the unwritten tail of a sparse image.</summary>
    public void Read(long offset, Span<byte> buffer)
    {
        buffer.Clear();
        if (offset >= _stream.Length)
        {
            return;
        }

        _stream.Position = offset;
        var done = 0;
        while (done < buffer.Length)
        {
            var read = _stream.Read(buffer[done..]);
            if (read == 0)
            {
                break;
            }

            done += read;
        }
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        _stream.Position = offset;
        _stream.Write(data);
    }

    public void Zero(long offset, long length, CancellationToken cancellationToken = default)
    {
        var chunk = new byte[(int)Math.Min(length, ZeroChunk)];
        _stream.Position = offset;
        while (length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(length, chunk.Length);
            _stream.Write(chunk, 0, count);
            length -= count;
        }
    }

    public void EnsureLength(long length)
    {
        if (_stream.Length < length)
        {
            _stream.SetLength(length);
        }
    }

    public void Flush() => _stream.Flush();
}
