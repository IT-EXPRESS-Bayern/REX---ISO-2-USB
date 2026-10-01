// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Writing.Raw;

/// <summary>
/// The GPT of an image that was copied to a larger disk, read as it is so that it can be moved to the end of the
/// disk and given one more partition without disturbing anything else: entry slots, GUIDs, attributes and the disk
/// GUID stay as they were. Only the layout GptBuilder itself produces is accepted (128 entries of 128 bytes right
/// after the header); anything else is left alone rather than rewritten on a guess.
/// </summary>
internal sealed class GptTable
{
    private const int SectorSize = 512;
    private const int ArrayBytes = GptBuilder.EntryArrayBytes;
    private const int ArraySectors = ArrayBytes / SectorSize;

    private readonly byte[] _entries;
    private readonly GptHeaderData _header;

    private GptTable(GptHeaderData header, byte[] entries, IReadOnlyList<(int Slot, GptPartition Partition)> partitions)
    {
        _header = header;
        _entries = entries;
        Partitions = partitions;
    }

    /// <summary>The used entries with their slot numbers (0-based).</summary>
    public IReadOnlyList<(int Slot, GptPartition Partition)> Partitions { get; }

    public Guid DiskGuid => _header.DiskGuid;

    public long FirstUsableLba => _header.FirstUsableLba;

    /// <summary>False when both copies already sit where a disk of this size has them.</summary>
    public bool NeedsMoving(long totalSectors) => _header.AlternateLba != totalSectors - 1 || _header.LastUsableLba != LastUsableLba(totalSectors);

    /// <summary>The slot after the last used one: new partitions are numbered after the existing ones.</summary>
    public int NextSlot => Partitions.Count == 0 ? 0 : Partitions[^1].Slot + 1;

    /// <summary>Why the table cannot be handled, or null when <paramref name="table"/> is usable.</summary>
    public static string? TryRead(Stream disk, out GptTable? table)
    {
        table = null;
        var header = new byte[SectorSize];
        Read(disk, SectorSize, header);
        if (GptHeaderCodec.TryRead(header, expectedLba: 1) is not { } data)
        {
            return "the primary GPT header is missing or damaged";
        }

        if (data.EntriesLba != 2 || data.EntryCount != GptBuilder.EntryCount || data.EntrySize != GptPartition.EntrySize)
        {
            return "the GPT does not use 128 entries of 128 bytes behind the header";
        }

        if (data.FirstUsableLba < 2 + ArraySectors)
        {
            return "the GPT claims sectors that belong to its own entry array";
        }

        var entries = new byte[ArrayBytes];
        Read(disk, 2L * SectorSize, entries);
        if (Crc32.Compute(entries) != data.EntriesCrc)
        {
            return "the GPT entry array does not match its checksum";
        }

        var partitions = new List<(int, GptPartition)>();
        for (var slot = 0; slot < GptBuilder.EntryCount; slot++)
        {
            if (GptPartition.Read(entries.AsSpan(slot * GptPartition.EntrySize, GptPartition.EntrySize)) is { } partition)
            {
                partitions.Add((slot, partition));
            }
        }

        table = new GptTable(data, entries, partitions);
        return null;
    }

    /// <summary>Last sector a partition may use on a disk of this size, with the backup entry array and header at the end.</summary>
    public static long LastUsableLba(long totalSectors) => totalSectors - 2 - ArraySectors;

    /// <summary>
    /// The table for a disk of <paramref name="totalSectors"/> sectors: both copies at their places, the existing
    /// entries unchanged and <paramref name="additions"/> in the given slots.
    /// </summary>
    public GptImage Relocate(long totalSectors, Mbr mbr, IEnumerable<(int Slot, GptPartition Partition)> additions)
    {
        var entries = (byte[])_entries.Clone();
        foreach (var (slot, partition) in additions)
        {
            partition.Write(entries.AsSpan(slot * GptPartition.EntrySize, GptPartition.EntrySize));
        }

        var crc = Crc32.Compute(entries);
        var last = LastUsableLba(totalSectors);
        var primary = Header(1, totalSectors - 1, 2, last, crc);
        var backup = Header(totalSectors - 1, 1, totalSectors - 1 - ArraySectors, last, crc);
        return new GptImage(SectorSize, totalSectors, mbr, primary, backup, entries);
    }

    private byte[] Header(long myLba, long alternateLba, long entriesLba, long lastUsable, uint entriesCrc)
    {
        var sector = new byte[SectorSize];
        GptHeaderCodec.Write(sector, _header with
        {
            MyLba = myLba,
            AlternateLba = alternateLba,
            EntriesLba = entriesLba,
            LastUsableLba = lastUsable,
            EntriesCrc = entriesCrc,
        });
        return sector;
    }

    private static void Read(Stream disk, long offset, byte[] buffer)
    {
        disk.Position = offset;
        disk.ReadExactly(buffer);
    }
}
