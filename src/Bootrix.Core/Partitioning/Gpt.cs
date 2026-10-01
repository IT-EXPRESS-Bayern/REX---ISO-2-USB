// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>A GUID partition table as read from a disk or image; see <see cref="GptBuilder"/> to create one.</summary>
public sealed class Gpt
{
    /// <summary>More than this many entries (or entries larger than <see cref="MaxEntrySize"/>) are treated as corruption rather than allocated.</summary>
    private const uint MaxEntryCount = 4096;
    private const uint MaxEntrySize = 1024;

    public required int SectorSize { get; init; }

    public required Guid DiskGuid { get; init; }

    public required long FirstUsableLba { get; init; }

    public required long LastUsableLba { get; init; }

    /// <summary>The disk size the header was written for; an image copied to a larger disk keeps its old value.</summary>
    public required long TotalSectors { get; init; }

    /// <summary>The used entries in slot order.</summary>
    public required IReadOnlyList<GptPartition> Partitions { get; init; }

    public required int EntryCount { get; init; }

    public required bool PrimaryValid { get; init; }

    public required bool BackupValid { get; init; }

    /// <summary>
    /// Reads the primary table, or the backup at the last sector when the primary is damaged.
    /// Returns null when neither header passes its CRC check, or both entry arrays fail theirs.
    /// </summary>
    public static Gpt? Read(Stream disk, int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var totalSectors = disk.Length / sectorSize;
        if (totalSectors < 3)
        {
            return null;
        }

        var primary = ReadCopy(disk, sectorSize, 1);
        var backup = ReadCopy(disk, sectorSize, totalSectors - 1);
        var source = primary ?? backup;
        if (source is null)
        {
            return null;
        }

        var header = source.Header;
        return new Gpt
        {
            SectorSize = sectorSize,
            DiskGuid = header.DiskGuid,
            FirstUsableLba = header.FirstUsableLba,
            LastUsableLba = header.LastUsableLba,
            TotalSectors = Math.Max(header.MyLba, header.AlternateLba) + 1,
            Partitions = source.Partitions,
            EntryCount = (int)header.EntryCount,
            PrimaryValid = primary is not null,
            BackupValid = backup is not null,
        };
    }

    private static Copy? ReadCopy(Stream disk, int sectorSize, long headerLba)
    {
        var sector = new byte[sectorSize];
        disk.Position = headerLba * sectorSize;
        if (disk.ReadAtLeast(sector, sector.Length, throwOnEndOfStream: false) < sector.Length)
        {
            return null;
        }

        var header = GptHeaderCodec.TryRead(sector, headerLba);
        if (header is null || header.EntryCount is 0 or > MaxEntryCount || header.EntrySize is < GptPartition.EntrySize or > MaxEntrySize
            || header.EntrySize % 8 != 0)
        {
            return null;
        }

        var arrayBytes = checked((int)(header.EntryCount * header.EntrySize));
        var array = new byte[arrayBytes];
        disk.Position = header.EntriesLba * sectorSize;
        if (disk.ReadAtLeast(array, array.Length, throwOnEndOfStream: false) < array.Length || Crc32.Compute(array) != header.EntriesCrc)
        {
            return null;
        }

        var partitions = new List<GptPartition>();
        for (var i = 0; i < header.EntryCount; i++)
        {
            var entry = GptPartition.Read(array.AsSpan(i * (int)header.EntrySize, (int)header.EntrySize));
            if (entry is not null)
            {
                partitions.Add(entry);
            }
        }

        return new Copy(header, partitions);
    }

    private sealed record Copy(GptHeaderData Header, List<GptPartition> Partitions);
}
