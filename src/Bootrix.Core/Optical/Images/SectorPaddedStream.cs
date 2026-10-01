// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Images;

/// <summary>
/// Presents <paramref name="length"/> bytes of the inner stream followed by zeros up to the next 2048-byte
/// boundary. IMAPI rejects streams that are not a whole number of sectors, and hybrid images or those cut
/// by a download tool often are not. The inner stream must start at its beginning.
/// </summary>
public sealed class SectorPaddedStream(Stream inner, long length) : Stream
{
    private readonly long _paddedLength = SectorMath.RoundUpToSector(length);
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _paddedLength;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        buffer = buffer[..(int)Math.Min(buffer.Length, _paddedLength - _position)];
        var total = 0;
        if (_position < length)
        {
            var wanted = (int)Math.Min(buffer.Length, length - _position);
            while (total < wanted)
            {
                var read = inner.Read(buffer.Slice(total, wanted - total));
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            // A source that ends before the length it announced is a damaged image; padding it would hide that.
            if (total < wanted)
            {
                throw new EndOfStreamException($"The image ended {length - _position - total} bytes before its announced length.");
            }
        }

        var padding = buffer.Length - total;
        buffer.Slice(total, padding).Clear();
        _position += buffer.Length;
        return buffer.Length;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _paddedLength + offset,
        };

        ArgumentOutOfRangeException.ThrowIfNegative(target);
        if (!inner.CanSeek)
        {
            return target == _position ? _position : throw new NotSupportedException("The image stream cannot seek.");
        }

        inner.Seek(Math.Min(target, length), SeekOrigin.Begin);

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
