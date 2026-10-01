// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Read wrapper around the compressed input. It counts the bytes consumed (for progress in compressed
/// bytes), always fills the caller's buffer unless the input ends (some decoders treat a short read as
/// the end of the data), and remembers the most recent bytes so that a reader that over-read, such as
/// <see cref="System.IO.Compression.DeflateStream"/> past the end of a gzip member, can step back.
/// </summary>
internal sealed class SourceStream(Stream inner, bool leaveOpen, int windowSize = 64 * 1024) : Stream
{
    private readonly byte[] _ring = new byte[windowSize];
    private long _pulled;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    /// <summary>Number of bytes handed out so far, net of <see cref="Rewind"/>.</summary>
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    /// <summary>Bytes taken from the underlying stream, including ones that were handed back by <see cref="Rewind"/>.</summary>
    public long Pulled => _pulled;

    /// <summary>How many bytes <see cref="Rewind"/> can currently give back.</summary>
    public long Rewindable => _position - Math.Max(0, _pulled - windowSize);

    public void Rewind(long count)
    {
        if (count < 0 || count > Rewindable)
        {
            throw new InvalidOperationException("Cannot rewind beyond the remembered window.");
        }

        _position -= count;
    }

    public int PeekByte()
    {
        Span<byte> one = stackalloc byte[1];
        if (Read(one) == 0)
        {
            return -1;
        }

        Rewind(1);
        return one[0];
    }

    /// <summary>A view for components that dispose the stream they are given.</summary>
    public Stream Borrow() => new Borrowed(this);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = TakeRemembered(buffer);
        while (total < buffer.Length)
        {
            var read = inner.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            Remember(buffer.Slice(total, read));
            total += read;
        }

        return total;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var total = TakeRemembered(buffer.Span);
        while (total < buffer.Length)
        {
            var read = await inner.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            Remember(buffer.Span.Slice(total, read));
            total += read;
        }

        return total;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Serves bytes that were pulled from the input before a rewind.</summary>
    private int TakeRemembered(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, _pulled - _position);
        if (count == 0)
        {
            return 0;
        }

        var index = (int)(_position % _ring.Length);
        var first = Math.Min(_ring.Length - index, count);
        _ring.AsSpan(index, first).CopyTo(buffer);
        _ring.AsSpan(0, count - first).CopyTo(buffer[first..]);
        _position += count;
        return count;
    }

    private void Remember(ReadOnlySpan<byte> data)
    {
        var start = _pulled;
        var tail = data;
        if (tail.Length > _ring.Length)
        {
            start += tail.Length - _ring.Length;
            tail = tail[^_ring.Length..];
        }

        var index = (int)(start % _ring.Length);
        var first = Math.Min(_ring.Length - index, tail.Length);
        tail[..first].CopyTo(_ring.AsSpan(index));
        tail[first..].CopyTo(_ring);
        _pulled += data.Length;
        _position += data.Length;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed class Borrowed(SourceStream owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => owner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => owner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => owner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            owner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
