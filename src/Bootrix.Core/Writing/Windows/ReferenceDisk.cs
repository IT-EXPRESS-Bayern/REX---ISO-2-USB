// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.IO;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// A tiny virtual disk (a fixed VHD) that holds one FAT32 partition. Windows attaches it, formats the
/// partition itself and so produces the boot code <see cref="WindowsFatBootCode"/> takes over. The partition
/// is already FAT32 when Windows sees it, because a disk without a file system gets no volume to format.
/// </summary>
public static class ReferenceDisk
{
    public const long Mib = 1024 * 1024;

    /// <summary>Where the partition starts; the usual alignment, so Windows treats the disk like any other.</summary>
    public const long PartitionOffset = Mib;

    /// <summary>Large enough for the smallest FAT32 that Windows formats (about 33 MB) with room to spare, small enough to write in no time.</summary>
    public const long DiskBytes = 72 * Mib;

    public const long PartitionBytes = DiskBytes - 2 * Mib;

    private const int FooterBytes = 512;
    private const int SectorBytes = 512;

    /// <summary>Seconds from the start of 1970 (Unix) to 2000-01-01 (the epoch of VHD time stamps).</summary>
    private const long VhdEpochUnixSeconds = 946_684_800;

    /// <summary>Writes the disk image, <see cref="DiskBytes"/> followed by the 512-byte VHD footer.</summary>
    public static void Write(Stream target, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.SetLength(DiskBytes + FooterBytes);

        var layout = new MbrBuilder()
            .WithSignature(0x42545258)
            .AddPartition(MbrPartitionType.Fat32Lba, PartitionOffset / SectorBytes, PartitionBytes / SectorBytes)
            .Build();
        var sector = layout.ToBytes();
        target.Position = 0;
        target.Write(sector);

        using (var partition = new StreamSlice(target, PartitionOffset, PartitionBytes))
        {
            FatFormatter.Format(partition, new FatFormatOptions
            {
                TotalBytes = PartitionBytes,
                Type = FatType.Fat32,
                Label = "BOOTRIX",
                HiddenSectors = (uint)(PartitionOffset / SectorBytes),
                AssumeZeroed = true,
            });
        }

        target.Position = DiskBytes;
        target.Write(Footer(DiskBytes, Guid.NewGuid(), time ?? TimeProvider.System));
        target.Flush();
    }

    /// <summary>The footer of a fixed VHD (Microsoft's "Virtual Hard Disk Image Format Specification"); all fields big endian.</summary>
    internal static byte[] Footer(long diskBytes, Guid uniqueId, TimeProvider time)
    {
        var footer = new byte[FooterBytes];
        "conectix"u8.CopyTo(footer);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(12), 0x00010000);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(16), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(24), (uint)(time.GetUtcNow().ToUnixTimeSeconds() - VhdEpochUnixSeconds));
        "btrx"u8.CopyTo(footer.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(32), 0x00010000);
        "Wi2k"u8.CopyTo(footer.AsSpan(36));
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(40), (ulong)diskBytes);
        BinaryPrimitives.WriteUInt64BigEndian(footer.AsSpan(48), (ulong)diskBytes);

        var (cylinders, heads, sectors) = Geometry(diskBytes / SectorBytes);
        BinaryPrimitives.WriteUInt16BigEndian(footer.AsSpan(56), (ushort)cylinders);
        footer[58] = (byte)heads;
        footer[59] = (byte)sectors;
        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(60), 2);
        uniqueId.TryWriteBytes(footer.AsSpan(68), bigEndian: true, out _);

        // The checksum is the one's complement of the sum of all other bytes.
        uint sum = 0;
        foreach (var value in footer)
        {
            sum += value;
        }

        BinaryPrimitives.WriteUInt32BigEndian(footer.AsSpan(64), ~sum);
        return footer;
    }

    /// <summary>The CHS values the specification derives from the size; Windows ignores them for fixed disks but checks that they are there.</summary>
    internal static (long Cylinders, int Heads, int SectorsPerTrack) Geometry(long totalSectors)
    {
        totalSectors = Math.Min(totalSectors, 65535L * 16 * 255);
        int sectorsPerTrack;
        int heads;
        long cylinderTimesHeads;

        if (totalSectors >= 65535L * 16 * 63)
        {
            sectorsPerTrack = 255;
            heads = 16;
            cylinderTimesHeads = totalSectors / sectorsPerTrack;
        }
        else
        {
            sectorsPerTrack = 17;
            cylinderTimesHeads = totalSectors / sectorsPerTrack;
            heads = (int)Math.Max((cylinderTimesHeads + 1023) / 1024, 4);
            if (cylinderTimesHeads >= (long)heads * 1024 || heads > 16)
            {
                sectorsPerTrack = 31;
                heads = 16;
                cylinderTimesHeads = totalSectors / sectorsPerTrack;
            }

            if (cylinderTimesHeads >= (long)heads * 1024)
            {
                sectorsPerTrack = 63;
                heads = 16;
                cylinderTimesHeads = totalSectors / sectorsPerTrack;
            }
        }

        return (cylinderTimesHeads / heads, heads, sectorsPerTrack);
    }
}
