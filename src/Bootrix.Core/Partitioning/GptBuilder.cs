// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>
/// Lays out a GPT for a disk of known size: 128 entries of 128 bytes after the header, a copy of
/// both at the end of the disk, and a protective MBR in front.
/// </summary>
public sealed class GptBuilder
{
    public const int EntryCount = 128;
    public const int EntryArrayBytes = EntryCount * GptPartition.EntrySize;

    private readonly List<GptPartition> _partitions = [];
    private readonly int _sectorSize;
    private readonly long _totalSectors;
    private Guid _diskGuid = Guid.NewGuid();
    private Mbr? _mbr;

    public GptBuilder(long totalSectors, int sectorSize = 512)
    {
        if (sectorSize is not (512 or 1024 or 2048 or 4096))
        {
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Sector sizes of 512 to 4096 bytes are supported.");
        }

        _sectorSize = sectorSize;
        _totalSectors = totalSectors;
        if (LastUsableLba < FirstUsableLba)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSectors), "The disk is too small for a GPT.");
        }
    }

    /// <summary>First sector after the primary header and entry array: 34 with 512-byte sectors, 6 with 4096.</summary>
    public long FirstUsableLba => 2 + EntrySectors;

    /// <summary>Last sector before the backup entry array: <c>total - 34</c> with 512-byte sectors, <c>total - 6</c> with 4096.</summary>
    public long LastUsableLba => _totalSectors - 2 - EntrySectors;

    public IReadOnlyList<GptPartition> Partitions => _partitions;

    private long EntrySectors => (EntryArrayBytes + _sectorSize - 1) / _sectorSize;

    public GptBuilder WithDiskGuid(Guid diskGuid)
    {
        _diskGuid = diskGuid;
        return this;
    }

    /// <summary>Replaces the protective MBR, for instance with a hybrid MBR built by <see cref="HybridMbr"/>.</summary>
    public GptBuilder WithMbr(Mbr mbr)
    {
        _mbr = mbr;
        return this;
    }

    public GptBuilder AddPartition(
        Guid typeGuid, long firstLba, long lastLba, string name = "",
        GptAttributes attributes = GptAttributes.None, Guid? uniqueGuid = null)
    {
        if (_partitions.Count == EntryCount)
        {
            throw new InvalidOperationException($"A GPT of this layout holds {EntryCount} partitions.");
        }

        if (typeGuid == Guid.Empty)
        {
            throw new ArgumentException("The all-zero type marks an unused entry.", nameof(typeGuid));
        }

        if (name.Length > GptPartition.MaxNameLength)
        {
            throw new ArgumentException($"A partition name holds {GptPartition.MaxNameLength} UTF-16 characters.", nameof(name));
        }

        if (firstLba < FirstUsableLba || lastLba > LastUsableLba || firstLba > lastLba)
        {
            throw new ArgumentOutOfRangeException(nameof(firstLba), $"Sectors {firstLba} to {lastLba} lie outside the usable range {FirstUsableLba} to {LastUsableLba}.");
        }

        var overlap = _partitions.FirstOrDefault(p => firstLba <= p.LastLba && p.FirstLba <= lastLba);
        if (overlap is not null)
        {
            throw new InvalidOperationException($"The partition at LBA {firstLba} overlaps the one at LBA {overlap.FirstLba}.");
        }

        _partitions.Add(new GptPartition(typeGuid, uniqueGuid ?? Guid.NewGuid(), firstLba, lastLba, attributes, name));
        return this;
    }

    public GptImage Build()
    {
        var entries = new byte[EntryArrayBytes];
        for (var i = 0; i < _partitions.Count; i++)
        {
            _partitions[i].Write(entries.AsSpan(i * GptPartition.EntrySize, GptPartition.EntrySize));
        }

        var entriesCrc = Crc32.Compute(entries);
        var primary = Header(myLba: 1, alternateLba: _totalSectors - 1, entriesLba: 2, entriesCrc);
        var backup = Header(myLba: _totalSectors - 1, alternateLba: 1, entriesLba: _totalSectors - 1 - EntrySectors, entriesCrc);

        return new GptImage(_sectorSize, _totalSectors, _mbr ?? MbrBuilder.Protective(_totalSectors), primary, backup, entries);
    }

    private byte[] Header(long myLba, long alternateLba, long entriesLba, uint entriesCrc)
    {
        var sector = new byte[_sectorSize];
        GptHeaderCodec.Write(sector, new GptHeaderData(
            myLba, alternateLba, FirstUsableLba, LastUsableLba, _diskGuid, entriesLba, EntryCount, GptPartition.EntrySize, entriesCrc));
        return sector;
    }
}
