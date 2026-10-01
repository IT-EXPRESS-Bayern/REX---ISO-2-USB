// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Partitioning;

/// <summary>Where partitions may start so flash controllers, 4K-sector disks and old BIOSes are all happy.</summary>
public static class PartitionAlignment
{
    public const long Mebibyte = 1024 * 1024;

    /// <summary>Anything that must hold a boot loader in the gap before the first partition (GRUB's core.img) needs this much.</summary>
    public const long BootloaderGapBytes = Mebibyte;

    private const long ClassicStartBytes = 63 * 512;
    private const long Kib64StartBytes = 64 * 1024;

    public static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    public static long AlignDown(long value, long alignment) => value / alignment * alignment;

    /// <summary>The modern default: LBA 2048 on 512-byte sectors, LBA 256 on 4096-byte sectors.</summary>
    public static long OneMebibyteLba(int sectorSize) => Mebibyte / sectorSize;

    /// <summary>
    /// First LBA for old-BIOS compatibility. The offsets are defined in 512-byte sectors, so on
    /// larger sectors they are rounded up to the next whole sector, which also keeps them 4K aligned.
    /// </summary>
    public static long LegacyStartLba(LegacyPartitionStart start, int sectorSize)
    {
        var bytes = start == LegacyPartitionStart.Lba63 ? ClassicStartBytes : Kib64StartBytes;
        return (bytes + sectorSize - 1) / sectorSize;
    }

    /// <summary>True when the byte offset of the partition is a multiple of <paramref name="boundaryBytes"/> (4 KiB suits both 512e and 4Kn).</summary>
    public static bool IsAligned(long lba, int sectorSize, long boundaryBytes = 4096) =>
        lba * sectorSize % boundaryBytes == 0;

    /// <summary>The smallest start LBA that leaves <see cref="BootloaderGapBytes"/> in front of the partition.</summary>
    public static long BootloaderGapLba(int sectorSize) => (BootloaderGapBytes + sectorSize - 1) / sectorSize;
}
