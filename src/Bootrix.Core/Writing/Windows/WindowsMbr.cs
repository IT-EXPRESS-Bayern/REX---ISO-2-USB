// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Errors;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// The master boot record of a Windows setup stick that starts on BIOS computers: the Syslinux code that hands
/// over to the active partition, plus the boot flag and type bytes the plan asks for. The partition table
/// itself comes from Windows; only what Windows or the formatter may have changed on the way is put right.
/// </summary>
public static class WindowsMbr
{
    /// <summary>Whether this plan needs boot code in the MBR at all: only MBR media with a BIOS start do.</summary>
    public static bool IsNeeded(MediaPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Scheme == PartitionScheme.Mbr && plan.BootMethod.HasFlag(BootMethod.WindowsBootmgrBios);
    }

    /// <summary>The 440 bytes of Syslinux's mbr.bin (MIT): find the active partition and run its boot sector.</summary>
    public static byte[] Bootstrap() => SyslinuxMbr.Code(gpt: false);

    /// <summary>
    /// Writes the boot code into the first 440 bytes of <paramref name="sector"/> and sets status and type of
    /// every partition as in the plan. The disk signature and the rest of the table stay as they are.
    /// </summary>
    /// <returns>True when a partition entry changed, so that the partition manager has to read the table again.</returns>
    /// <exception cref="BootrixException">The sector holds no MBR, or a partition of the plan is not in it.</exception>
    public static bool Apply(Span<byte> sector, MediaPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!Mbr.TryParse(sector, out var mbr))
        {
            throw new BootrixException(ErrorCode.LayoutRejected, "sector 0 holds no MBR");
        }

        var entries = mbr.Entries.ToArray();
        var wanted = new HashSet<int>();
        foreach (var partition in plan.Partitions.Where(p => p.MbrType != MbrPartitionType.Empty))
        {
            var start = partition.StartLba(plan.SectorSize);
            var count = partition.SectorCount(plan.SectorSize);
            var slot = Array.FindIndex(entries, e => !e.IsEmpty && e.StartLba == start && e.SectorCount == count);
            if (slot < 0)
            {
                throw new BootrixException(ErrorCode.LayoutRejected, $"the {partition.Role} partition at sector {start} is not in the partition table");
            }

            wanted.Add(slot);
            entries[slot] = entries[slot] with
            {
                Status = partition.Active ? MbrEntry.ActiveStatus : (byte)0,
                Type = partition.MbrType,
            };
        }

        // Two active partitions make the boot code stop with an error; anything the plan does not know is not allowed to be one.
        for (var slot = 0; slot < entries.Length; slot++)
        {
            if (!wanted.Contains(slot) && entries[slot].IsActive)
            {
                entries[slot] = entries[slot] with { Status = 0 };
            }
        }

        var changed = !entries.SequenceEqual(mbr.Entries);
        (mbr with { Bootstrap = Bootstrap(), Entries = entries }).WriteTo(sector);
        return changed;
    }
}
