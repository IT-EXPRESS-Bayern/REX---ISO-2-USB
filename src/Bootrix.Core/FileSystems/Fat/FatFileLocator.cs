// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.FileSystems.Fat;

/// <summary>
/// Reads just enough of a FAT volume to answer where a file in the root directory lives on the disk.
/// Boot loaders that are told sector numbers instead of names (Syslinux, GRUB's blocklists) need
/// exactly that; the answer must come from the volume itself, whatever driver wrote the file.
/// </summary>
internal sealed class FatFileLocator
{
    private const int DirectoryEntryBytes = 32;
    private const byte LongNameAttributes = 0x0F;
    private const byte VolumeLabelAttribute = 0x08;
    private const byte DirectoryAttribute = 0x10;

    private readonly Stream _volume;
    private readonly int _bytesPerSector;
    private readonly int _sectorsPerCluster;
    private readonly long _fatStartSector;
    private readonly long _rootStartSector;
    private readonly int _rootSectors;
    private readonly long _dataStartSector;
    private readonly uint _rootCluster;
    private readonly long _clusterCount;
    private readonly byte[] _fatSector;
    private long _fatSectorLoaded = -1;

    private FatFileLocator(Stream volume, byte[] boot)
    {
        _volume = volume;
        _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
        _sectorsPerCluster = boot[13];
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14));
        var fats = boot[16];
        var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17));
        long total = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19));
        if (total == 0)
        {
            total = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32));
        }

        long fatSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22));
        if (fatSectors == 0)
        {
            fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36));
        }

        if (_bytesPerSector is < 512 or > 4096 || !int.IsPow2(_bytesPerSector) || _sectorsPerCluster == 0 || !int.IsPow2(_sectorsPerCluster)
            || reserved == 0 || fats == 0 || fatSectors == 0 || total == 0)
        {
            throw Invalid("the boot sector is not that of a FAT volume");
        }

        _fatStartSector = reserved;
        _rootSectors = (rootEntries * DirectoryEntryBytes + _bytesPerSector - 1) / _bytesPerSector;
        _rootStartSector = reserved + fats * fatSectors;
        _dataStartSector = _rootStartSector + _rootSectors;
        _clusterCount = (total - _dataStartSector) / _sectorsPerCluster;
        Type = _clusterCount < 4085 ? FatType.Fat12 : _clusterCount < 65525 ? FatType.Fat16 : FatType.Fat32;
        _rootCluster = Type == FatType.Fat32 ? BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44)) : 0;
        _fatSector = new byte[_bytesPerSector];
    }

    public FatType Type { get; }

    public int BytesPerSector => _bytesPerSector;

    public static FatFileLocator Open(Stream volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        var boot = new byte[512];
        Read(volume, 0, boot);
        return new FatFileLocator(volume, boot);
    }

    /// <summary>First cluster and size of a file in the root directory, by its 11-character short name ("LDLINUX SYS"); null when absent.</summary>
    public (uint FirstCluster, uint Size)? FindInRoot(string shortName)
    {
        var wanted = System.Text.Encoding.ASCII.GetBytes(shortName.PadRight(11));
        var entry = new byte[DirectoryEntryBytes];
        foreach (var offset in RootDirectoryOffsets())
        {
            Read(_volume, offset, entry);
            if (entry[0] == 0x00)
            {
                return null;
            }

            if (entry[0] == 0xE5 || (entry[11] & LongNameAttributes) == LongNameAttributes || (entry[11] & (VolumeLabelAttribute | DirectoryAttribute)) != 0)
            {
                continue;
            }

            if (entry.AsSpan(0, 11).SequenceEqual(wanted))
            {
                var cluster = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(26)) | ((uint)BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(20)) << 16);
                return (Type == FatType.Fat32 ? cluster : cluster & 0xFFFF, BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(28)));
            }
        }

        return null;
    }

    /// <summary>
    /// The volume sector numbers of the first <paramref name="sectorCount"/> sectors of a file, in file order.
    /// Sectors are those of the volume (sector 0 is the boot sector), in units of its own sector size.
    /// </summary>
    public long[] FileSectors(uint firstCluster, int sectorCount)
    {
        var sectors = new long[sectorCount];
        var filled = 0;
        var cluster = firstCluster;
        var guard = 0L;
        while (filled < sectorCount)
        {
            if (cluster < 2 || cluster >= _clusterCount + 2 || ++guard > _clusterCount)
            {
                throw Invalid("the cluster chain of the file ends early or loops");
            }

            var first = _dataStartSector + ((long)(cluster - 2) * _sectorsPerCluster);
            for (var i = 0; i < _sectorsPerCluster && filled < sectorCount; i++)
            {
                sectors[filled++] = first + i;
            }

            if (filled < sectorCount)
            {
                cluster = NextCluster(cluster);
            }
        }

        return sectors;
    }

    private IEnumerable<long> RootDirectoryOffsets()
    {
        if (Type != FatType.Fat32)
        {
            for (var i = 0L; i < (long)_rootSectors * _bytesPerSector; i += DirectoryEntryBytes)
            {
                yield return (_rootStartSector * _bytesPerSector) + i;
            }

            yield break;
        }

        var cluster = _rootCluster;
        var guard = 0L;
        var clusterBytes = (long)_sectorsPerCluster * _bytesPerSector;
        while (cluster >= 2 && cluster < _clusterCount + 2 && ++guard <= _clusterCount)
        {
            var start = (_dataStartSector + ((long)(cluster - 2) * _sectorsPerCluster)) * _bytesPerSector;
            for (var i = 0L; i < clusterBytes; i += DirectoryEntryBytes)
            {
                yield return start + i;
            }

            cluster = NextCluster(cluster);
        }
    }

    private uint NextCluster(uint cluster)
    {
        var entryBits = Type switch { FatType.Fat12 => 12, FatType.Fat16 => 16, _ => 32 };
        var bit = (long)cluster * entryBits;
        var byteOffset = bit / 8;
        var sector = _fatStartSector + (byteOffset / _bytesPerSector);
        var inSector = (int)(byteOffset % _bytesPerSector);

        uint value;
        if (Type == FatType.Fat12)
        {
            value = ReadFatByte(sector, inSector) | ((uint)ReadFatByte(sector + ((inSector + 1) / _bytesPerSector), (inSector + 1) % _bytesPerSector) << 8);
            value = (cluster & 1) == 0 ? value & 0xFFF : value >> 4;
            return value >= 0xFF8 ? 0 : value;
        }

        if (Type == FatType.Fat16)
        {
            value = ReadFatByte(sector, inSector) | ((uint)ReadFatByte(sector, inSector + 1) << 8);
            return value >= 0xFFF8 ? 0 : value;
        }

        value = ReadFatByte(sector, inSector) | ((uint)ReadFatByte(sector, inSector + 1) << 8)
            | ((uint)ReadFatByte(sector, inSector + 2) << 16) | ((uint)ReadFatByte(sector, inSector + 3) << 24);
        value &= 0x0FFFFFFF;
        return value >= 0x0FFFFFF8 ? 0 : value;
    }

    private byte ReadFatByte(long sector, int offset)
    {
        if (sector != _fatSectorLoaded)
        {
            Read(_volume, sector * _bytesPerSector, _fatSector);
            _fatSectorLoaded = sector;
        }

        return _fatSector[offset];
    }

    private static void Read(Stream stream, long offset, byte[] buffer)
    {
        stream.Position = offset;
        stream.ReadExactly(buffer);
    }

    private static InvalidDataException Invalid(string detail) => new(detail);
}
