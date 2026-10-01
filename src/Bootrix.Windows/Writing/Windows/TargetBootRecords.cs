// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>The sectors outside the files of a Windows medium: the MBR with its boot code, the boot sectors of the main volume, the UEFI:NTFS partition.</summary>
internal static class TargetBootRecords
{
    /// <summary>Puts the boot code into the MBR and the flags of the plan into its table. Returns whether the table changed.</summary>
    public static bool WriteMbr(StorageDevice device, MediaPlan plan, ILogger log, CancellationToken cancellationToken)
    {
        using var disk = DiskAccess.Open(device, write: true, cancellationToken);
        using var stream = new BlockDeviceStream(disk, 0, disk.Length);

        var sector = new byte[plan.SectorSize];
        stream.ReadExactly(sector);
        var changed = WindowsMbr.Apply(sector, plan);
        stream.Position = 0;
        stream.Write(sector);
        stream.Flush();

        if (changed)
        {
            // Windows keeps its own copy of the table; without this it would not notice the corrected flag or type.
            DeviceIo.TryControl(disk.Handle, Ioctl.DiskUpdateProperties);
            log.LogInformation("Corrected the boot flag or type in the partition table of disk {Disk}", device.DiskNumber);
        }

        log.LogInformation("Wrote the MBR boot code on disk {Disk}", device.DiskNumber);
        return changed;
    }

    /// <summary>Reads the MBR back and checks that the code and the table are exactly what <see cref="WindowsMbr.Apply"/> makes of them.</summary>
    public static void VerifyMbr(StorageDevice device, MediaPlan plan, CancellationToken cancellationToken)
    {
        var onDisk = Read(device, 0, plan.SectorSize, cancellationToken);
        var expected = (byte[])onDisk.Clone();
        var changed = WindowsMbr.Apply(expected, plan);
        if (changed || !expected.AsSpan().SequenceEqual(onDisk))
        {
            throw new BootrixException(ErrorCode.VerifyMismatch, "MBR boot code or partition flags differ after writing") { Arguments = [0L] };
        }
    }

    /// <summary>The boot code in the reserved sectors of the main FAT32 volume is the one that was meant to go there.</summary>
    public static void VerifyBootSectors(StorageDevice device, PlannedPartition main, FatBootSectors expected, CancellationToken cancellationToken)
    {
        var length = expected.SectorCount * expected.BytesPerSector;
        if (!WindowsFatBootCode.Matches(Read(device, main.StartBytes, length, cancellationToken), expected))
        {
            throw new BootrixException(ErrorCode.VerifyMismatch, "boot sectors of the main partition differ after writing") { Arguments = [main.StartBytes] };
        }
    }

    public static void VerifyPartition(StorageDevice device, PlannedPartition partition, byte[] expected, CancellationToken cancellationToken)
    {
        if (!Read(device, partition.StartBytes, expected.Length, cancellationToken).AsSpan().SequenceEqual(expected))
        {
            throw new BootrixException(ErrorCode.VerifyMismatch, $"the {partition.Role} partition differs after writing") { Arguments = [partition.StartBytes] };
        }
    }

    private static byte[] Read(StorageDevice device, long offset, int length, CancellationToken cancellationToken)
    {
        using var disk = DiskAccess.Open(device, write: false, cancellationToken);
        using var stream = new BlockDeviceStream(disk, 0, disk.Length);
        stream.Position = offset;
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
