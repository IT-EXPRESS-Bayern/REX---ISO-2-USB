// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

/// <summary>
/// Base for the decoded view of a disk image: read-only, seekable, with a fixed length.
/// Derived classes only implement positional reads; bounds, seeking and zero-length reads are handled here.
/// </summary>
public abstract class ReadOnlyVolumeStream : Stream
{
    private readonly long _length;
    private long _position;

    protected ReadOnlyVolumeStream(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _length = length;
    }

    public override bool CanRead => !Disposed;

    public override bool CanSeek => !Disposed;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return _position;
        }
        set
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    private protected bool Disposed { get; private set; }

    /// <summary>
    /// Fills <paramref name="destination"/> from the volume at <paramref name="position"/>. The caller guarantees
    /// that the range lies inside the volume; the method may return fewer bytes than requested but never zero.
    /// </summary>
    protected abstract int ReadCore(long position, Span<byte> destination);

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        var total = 0;
        while (total < buffer.Length && _position < _length)
        {
            var wanted = (int)Math.Min(buffer.Length - total, _length - _position);
            var read = ReadCore(_position, buffer.Slice(total, wanted));
            if (read <= 0)
            {
                throw new InvalidOperationException("The image reader returned no data inside the volume.");
            }

            total += read;
            _position += read;
        }

        return total;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        ArgumentOutOfRangeException.ThrowIfNegative(target, nameof(offset));
        _position = target;
        return target;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
