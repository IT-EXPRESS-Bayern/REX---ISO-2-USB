// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Reading;

/// <summary>
/// Read-only stream over a disc, for the parts of Bootrix that want to look into the file system
/// of a disc before copying it. File system readers ask for a few bytes at a time, so whole
/// blocks of sectors are cached.
/// </summary>
internal sealed class SectorReaderStream : Stream
{
    private const int CacheSectors = 32;

    private readonly ISectorReader _reader;
    private readonly byte[] _cache = new byte[CacheSectors * SectorMath.SectorSize];
    private long _cacheStart = -1;
    private int _cacheSectors;
    private long _position;

    public SectorReaderStream(ISectorReader reader)
    {
        _reader = reader;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => SectorMath.ToBytes(_reader.SectorCount);

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (!buffer.IsEmpty && _position < Length)
        {
            var lba = _position / SectorMath.SectorSize;
            if (!Fill(lba))
            {
                throw new IOException($"Sector {lba} of {_reader.Name} cannot be read.");
            }

            var inCache = (int)(_position - _cacheStart * SectorMath.SectorSize);
            var available = (int)Math.Min(_cacheSectors * SectorMath.SectorSize - inCache, Length - _position);
            var take = Math.Min(available, buffer.Length);
            _cache.AsSpan(inCache, take).CopyTo(buffer);
            buffer = buffer[take..];
            _position += take;
            total += take;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private bool Fill(long lba)
    {
        if (_cacheStart >= 0 && lba >= _cacheStart && lba < _cacheStart + _cacheSectors)
        {
            return true;
        }

        var start = lba / CacheSectors * CacheSectors;
        var want = (int)Math.Min(CacheSectors, _reader.SectorCount - start);
        var result = _reader.Read(start, want, _cache.AsSpan(0, want * SectorMath.SectorSize));
        if (result.SectorsRead <= lba - start)
        {
            // The block failed or ended before the wanted sector; try that single sector so one bad sector does not hide its neighbours.
            result = _reader.Read(lba, 1, _cache.AsSpan(0, SectorMath.SectorSize));
            if (result.SectorsRead < 1)
            {
                _cacheStart = -1;
                return false;
            }

            start = lba;
        }

        _cacheStart = start;
        _cacheSectors = result.SectorsRead;
        return true;
    }
}
