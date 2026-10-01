// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

/// <summary>
/// Presents a window of a block device as an ordinary seekable stream. Writes that do not cover
/// whole sectors are completed by reading the surrounding sectors first, and a small write-back
/// cache keeps the many tiny writes of a file system formatter from turning into as many device
/// requests. <see cref="Flush"/> must be called before the data is relied upon.
/// </summary>
public sealed class BlockDeviceStream : Stream
{
    private const int CacheBlockSectors = 256;

    private readonly IBlockDevice _device;
    private readonly long _start;
    private readonly long _length;
    private readonly int _blockBytes;
    private readonly AlignedBuffer _block;
    private long _blockOffset = -1;
    private bool _dirty;
    private long _position;

    public BlockDeviceStream(IBlockDevice device, long start, long length)
    {
        if (start % device.SectorSize != 0)
        {
            throw new ArgumentException("The window must start on a sector boundary.", nameof(start));
        }

        _device = device;
        _start = start;
        _length = length;
        _blockBytes = device.SectorSize * CacheBlockSectors;
        _block = new AlignedBuffer(_blockBytes, device.BufferAlignment);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length && _position < _length)
        {
            var (blockStart, inBlock) = Locate(_position);
            LoadBlock(blockStart);
            var take = (int)Math.Min(Math.Min(buffer.Length - total, _blockBytes - inBlock), _length - _position);
            _block.GetSpan().Slice(inBlock, take).CopyTo(buffer[total..]);
            total += take;
            _position += take;
        }

        return total;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_position + buffer.Length > _length)
        {
            throw new IOException("Write beyond the end of the device window.");
        }

        var written = 0;
        while (written < buffer.Length)
        {
            var (blockStart, inBlock) = Locate(_position);
            LoadBlock(blockStart);
            var take = Math.Min(buffer.Length - written, _blockBytes - inBlock);
            buffer.Slice(written, take).CopyTo(_block.GetSpan()[inBlock..]);
            _dirty = true;
            written += take;
            _position += take;
        }
    }

    public override void Flush()
    {
        WriteBackBlock();
        _device.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WriteBackBlock();
            ((IDisposable)_block).Dispose();
        }

        base.Dispose(disposing);
    }

    private (long BlockStart, int InBlock) Locate(long position)
    {
        var blockStart = position / _blockBytes * _blockBytes;
        return (blockStart, (int)(position - blockStart));
    }

    private void LoadBlock(long blockStart)
    {
        if (blockStart == _blockOffset)
        {
            return;
        }

        WriteBackBlock();
        var available = (int)Math.Min(_blockBytes, Math.Max(0, _device.Length - (_start + blockStart)));
        var span = _block.GetSpan();
        span.Clear();
        if (available > 0)
        {
            var sector = _device.SectorSize;
            var aligned = (available + sector - 1) / sector * sector;
            _device.Read(_start + blockStart, span[..aligned]);
        }

        _blockOffset = blockStart;
    }

    private void WriteBackBlock()
    {
        if (!_dirty || _blockOffset < 0)
        {
            return;
        }

        var sector = _device.SectorSize;
        var remaining = (int)Math.Min(_blockBytes, Math.Max(0, _device.Length - (_start + _blockOffset)));
        var aligned = (remaining + sector - 1) / sector * sector;
        _device.Write(_start + _blockOffset, _block.GetSpan()[..aligned]);
        _dirty = false;
    }
}
