// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;

namespace Bootrix.Core.IO;

/// <summary>
/// A fixed window into a seekable stream, used to address one partition of a disk image or
/// device as if it started at offset zero. Reads past the end return fewer bytes (like a file),
/// writes past the end are refused rather than silently spilling into the next partition.
/// </summary>
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "Reads as a noun for the thing it is: a slice of a stream.")]
public sealed class StreamSlice : Stream
{
    private readonly Stream _inner;
    private readonly long _offset;
    private readonly long _length;
    private readonly bool _leaveOpen;
    private long _position;

    public StreamSlice(Stream inner, long offset, long length, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!inner.CanSeek)
        {
            throw new ArgumentException("The underlying stream must be seekable.", nameof(inner));
        }

        if (offset > long.MaxValue - length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Slice end overflows.");
        }

        _inner = inner;
        _offset = offset;
        _length = length;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => true;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        buffer = Clip(buffer);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        _inner.Position = _offset + _position;
        var read = _inner.Read(buffer);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        buffer = Clip(buffer);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        _inner.Position = _offset + _position;
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ValidateBuffer(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureFits(buffer.Length);
        if (buffer.IsEmpty)
        {
            return;
        }

        _inner.Position = _offset + _position;
        _inner.Write(buffer);
        _position += buffer.Length;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureFits(buffer.Length);
        if (buffer.IsEmpty)
        {
            return;
        }

        _inner.Position = _offset + _position;
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += buffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
        {
            throw new IOException("Seek before the beginning of the slice.");
        }

        _position = target;
        return _position;
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override void SetLength(long value) =>
        throw new NotSupportedException("A slice has a fixed length.");

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private Span<byte> Clip(Span<byte> buffer) =>
        buffer[..(int)Math.Min(buffer.Length, Math.Max(0, _length - _position))];

    private Memory<byte> Clip(Memory<byte> buffer) =>
        buffer[..(int)Math.Min(buffer.Length, Math.Max(0, _length - _position))];

    private void EnsureFits(int count)
    {
        if (count > 0 && _position + count > _length)
        {
            throw new IOException("Write beyond the end of the slice.");
        }
    }

    private static void ValidateBuffer(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("Offset and count exceed the buffer.");
        }
    }
}
