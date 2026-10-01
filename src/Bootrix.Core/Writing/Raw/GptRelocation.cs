// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Partitioning;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Writing.Raw;

public enum GptRelocationResult
{
    /// <summary>The disk has no plain GPT to move: no GPT, a damaged or unusual one, a hybrid MBR, 4Kn sectors.</summary>
    NotApplicable,

    AlreadyAtEnd,

    Moved,
}

/// <summary>
/// Moves the backup GPT of a disk image to the end of the disk it was written to. A GPT image carries its backup
/// behind its own last partition, so on a larger disk tools complain that the table does not match the disk size and
/// the space behind the image cannot be used. Partitions, GUIDs and attributes stay exactly as the image has them.
/// </summary>
public static class GptRelocation
{
    public static GptRelocationResult MoveToDiskEnd(IBlockDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.SectorSize != PersistencePlacer.SectorSize)
        {
            return GptRelocationResult.NotApplicable;
        }

        var totalSectors = device.Length / PersistencePlacer.SectorSize;
        using var disk = new BlockDeviceStream(device, 0, totalSectors * PersistencePlacer.SectorSize);
        var sector = new byte[PersistencePlacer.SectorSize];
        disk.ReadExactly(sector);
        if (!Mbr.TryParse(sector, out var mbr) || !IsPlainProtective(mbr) || GptTable.TryRead(disk, out var table) is not null)
        {
            return GptRelocationResult.NotApplicable;
        }

        if (!table!.NeedsMoving(totalSectors))
        {
            return GptRelocationResult.AlreadyAtEnd;
        }

        table.Relocate(totalSectors, PersistenceInstaller.WidenProtectiveEntry(mbr, totalSectors), []).WriteTo(disk);
        disk.Flush();
        return GptRelocationResult.Moved;
    }

    /// <summary>One entry, and it is the protective one; a hybrid MBR mirrors partitions and must not be touched.</summary>
    internal static bool IsPlainProtective(Mbr mbr)
    {
        var used = mbr.Entries.Where(entry => !entry.IsEmpty).ToList();
        return used.Count == 1 && used[0].Type == MbrPartitionType.GptProtective;
    }
}
