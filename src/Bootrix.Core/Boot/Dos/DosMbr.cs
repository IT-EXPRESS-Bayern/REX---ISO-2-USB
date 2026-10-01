// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Boot.Dos;

/// <summary>
/// The MBR code of a DOS stick: Syslinux's MBR (MIT license), which starts the one active partition
/// through the BIOS extensions when they exist and through CHS otherwise.
/// </summary>
public static class DosMbr
{
    private const int TableOffset = 446;

    /// <summary>
    /// The 440 bytes of boot code. With <paramref name="forceBootDrive"/> the variant that always passes drive
    /// 0x80 to the volume boot sector is returned, for old BIOSes that report another number for a USB stick.
    /// </summary>
    public static byte[] Bootstrap(bool forceBootDrive = false) => DosAssets.Mbr(forceBootDrive ? "mbr_f.bin" : "mbr.bin");

    /// <summary>
    /// Replaces the boot code in sector 0 and keeps the disk signature and the partition table. The BIOS needs exactly one
    /// active partition that is a FAT volume: when Windows dropped the flag of a table with a single partition, it is set
    /// again; every other table that would leave the BIOS nothing to start is refused.
    /// </summary>
    public static byte[] Install(ReadOnlySpan<byte> sector, bool forceBootDrive = false)
    {
        if (!Mbr.TryParse(sector, out var mbr))
        {
            throw NotBootable("sector 0 has no 0x55AA signature");
        }

        var result = sector[..Mbr.SectorSize].ToArray();
        var used = Enumerable.Range(0, Mbr.EntryCount).Where(i => !mbr.Entries[i].IsEmpty).ToList();
        var active = used.Where(i => mbr.Entries[i].IsActive).ToList();
        if (active.Count == 0 && used.Count == 1)
        {
            active = used;
            result[TableOffset + used[0] * MbrEntry.Size] = MbrEntry.ActiveStatus;
        }

        if (active.Count != 1)
        {
            throw NotBootable($"{active.Count} of {used.Count} partitions are marked active");
        }

        var type = mbr.Entries[active[0]].Type;
        if (!IsFat(type))
        {
            throw NotBootable($"the active partition has type 0x{type:X2}");
        }

        result.AsSpan(0, Mbr.BootstrapLength).Clear();
        Bootstrap(forceBootDrive).CopyTo(result, 0);
        return result;
    }

    private static bool IsFat(byte type) => type is
        MbrPartitionType.Fat12 or MbrPartitionType.Fat16Small or MbrPartitionType.Fat16 or
        MbrPartitionType.Fat32Chs or MbrPartitionType.Fat32Lba or MbrPartitionType.Fat16Lba;

    private static BootrixException NotBootable(string detail) =>
        new(ErrorCode.DosMbrNotBootable, detail) { Arguments = [detail] };
}
