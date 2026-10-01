// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Optical.Images;

/// <summary>
/// The 2048 bytes of user data of every sector of a MODE1 track. In a 2352-byte raw sector they sit after
/// 12 bytes of sync and 4 of header (the ECC and EDC bytes behind them are of no use once the drive writes
/// its own); a MODE1/2048 track is already in that form and is passed through.
/// </summary>
internal sealed class Mode1UserDataStream : Stream
{
    public const int RawSectorSize = 2352;
    public const int UserDataOffset = 16;

    private const int BlockSectors = 16;

    // 00, ten times FF, 00: the sync pattern that starts every raw CD sector (ECMA-130, section 14.1).
    private static readonly byte[] Sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private readonly Stream _bin;
    private readonly bool _raw;
    private readonly long _startSector;
    private readonly long _sectorCount;
    private readonly byte[] _block;
    private long _blockFirstSector = -1;
    private int _blockSectors;
    private long _position;

    public Mode1UserDataStream(Stream bin, bool rawSectors, long startSector, long sectorCount)
    {
        _bin = bin;
        _raw = rawSectors;
        _startSector = startSector;
        _sectorCount = sectorCount;
        _block = new byte[BlockSectors * (rawSectors ? RawSectorSize : SectorMath.SectorSize)];
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _sectorCount * SectorMath.SectorSize;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    /// <summary>Checks that a raw sector really is MODE1; a cue sheet that claims MODE1/2352 for a MODE2 or scrambled image would otherwise burn garbage.</summary>
    public static bool IsMode1Sector(ReadOnlySpan<byte> rawSector) =>
        rawSector.Length >= UserDataOffset && rawSector[..Sync.Length].SequenceEqual(Sync) && rawSector[15] == 1;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (!buffer.IsEmpty && _position < Length)
        {
            var sector = _position / SectorMath.SectorSize;
            LoadBlock(sector);

            var rawSize = _raw ? RawSectorSize : SectorMath.SectorSize;
            var inSector = (int)(_position % SectorMath.SectorSize);
            var sectorInBlock = (int)(sector - _blockFirstSector);
            var take = Math.Min(buffer.Length, SectorMath.SectorSize - inSector);
            var source = sectorInBlock * rawSize + (_raw ? UserDataOffset : 0) + inSector;
            _block.AsSpan(source, take).CopyTo(buffer);

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bin.Dispose();
        }

        base.Dispose(disposing);
    }

    private void LoadBlock(long sector)
    {
        if (_blockFirstSector >= 0 && sector >= _blockFirstSector && sector < _blockFirstSector + _blockSectors)
        {
            return;
        }

        var rawSize = _raw ? RawSectorSize : SectorMath.SectorSize;
        var first = sector / BlockSectors * BlockSectors;
        var count = (int)Math.Min(BlockSectors, _sectorCount - first);
        _bin.Position = (_startSector + first) * rawSize;
        _bin.ReadExactly(_block.AsSpan(0, count * rawSize));

        if (_raw)
        {
            for (var i = 0; i < count; i++)
            {
                if (!IsMode1Sector(_block.AsSpan(i * rawSize, rawSize)))
                {
                    throw new BootrixException(ErrorCode.ImageUnsupported, $"sector {first + i} is not a MODE1/2352 sector")
                    {
                        Arguments = [$"sector {first + i} of the BIN file is not MODE1/2352 although the cue sheet says so"],
                    };
                }
            }
        }

        _blockFirstSector = first;
        _blockSectors = count;
    }
}
