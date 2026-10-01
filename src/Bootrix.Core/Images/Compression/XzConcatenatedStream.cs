// SPDX-License-Identifier: GPL-3.0-or-later
using SharpCompress.Compressors.Xz;

namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Reads a sequence of xz streams as one. The xz tools accept concatenated streams, optionally separated
/// by zero padding in multiples of four bytes; the underlying decoder stops after the first stream.
/// </summary>
internal sealed class XzConcatenatedStream(SourceStream source) : Stream
{
    private XZStream? _current;
    private bool _finished;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (!_finished)
        {
            if (_current is null && !OpenNextStream())
            {
                _finished = true;
                break;
            }

            var read = _current!.Read(buffer);
            if (read > 0)
            {
                return read;
            }

            _current.Dispose();
            _current = null;
        }

        return 0;
    }

    private bool OpenNextStream()
    {
        var padding = 0L;
        while (source.PeekByte() == 0)
        {
            source.ReadByte();
            padding++;
        }

        if (padding % 4 != 0)
        {
            throw new InvalidDataException("xz stream padding is not a multiple of four bytes.");
        }

        if (source.PeekByte() < 0)
        {
            return false;
        }

        _current = new XZStream(source.Borrow());
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
            _current?.Dispose();
        }

        base.Dispose(disposing);
    }
}
