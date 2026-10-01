// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>The disk operations of a Windows setup write on a physical disk: boot sectors, the MBR, the caches of the volumes.</summary>
internal sealed class PhysicalTargetOps(WriteServices services) : ITargetOps
{
    private readonly ILogger _log = services.LoggerFor<PhysicalTargetOps>();

    public StorageDevice Current(MediaWriteTarget target) => services.Disks.Find(target.Device.DevicePath) ?? target.Device;

    public void FlushVolume(string volumeGuidPath) => VolumeFlusher.Flush(volumeGuidPath);

    /// <summary>Flushes every volume and the disk, then locks the volumes, which dismounts them; Windows mounts them again when the next file is opened.</summary>
    public void DropCaches(MediaWriteTarget target, StorageDevice device, CancellationToken cancellationToken)
    {
        foreach (var partition in target.Prepared?.Partitions ?? [])
        {
            if (partition.Volume is { } volume)
            {
                VolumeFlusher.Flush(volume.VolumeGuidPath);
            }
        }

        using (var disk = DiskAccess.Open(device, write: true, cancellationToken))
        {
            disk.Flush();
        }

        using var locks = VolumeLockSet.Acquire(device, _log, cancellationToken);
    }

    /// <summary>
    /// Puts the boot code into the MBR and the flags of the plan into its table. Returns whether the table changed.
    /// Exactly one sector is written: with the classic partition offsets the first volume starts at sector 63 or 128,
    /// and a write that reached into it would be refused (or would overwrite what the file system has not flushed yet).
    /// </summary>
    public bool WriteMbr(StorageDevice device, MediaPlan plan, CancellationToken cancellationToken)
    {
        using var disk = DiskAccess.Open(device, write: true, cancellationToken);
        using var buffer = new AlignedBuffer(plan.SectorSize, disk.BufferAlignment);
        var sector = buffer.GetSpan();

        if (disk.Read(0, sector) != sector.Length)
        {
            throw new IOException($"Could not read sector 0 of disk {device.DiskNumber}.");
        }

        var changed = WindowsMbr.Apply(sector, plan);
        disk.Write(0, sector);
        disk.Flush();

        if (changed)
        {
            // Windows keeps its own copy of the table; without this it would not notice the corrected flag or type.
            DeviceIo.TryControl(disk.Handle, Ioctl.DiskUpdateProperties);
            _log.LogInformation("Corrected the boot flag or type in the partition table of disk {Disk}", device.DiskNumber);
        }

        _log.LogInformation("Wrote the MBR boot code on disk {Disk}", device.DiskNumber);
        return changed;
    }

    /// <summary>Reads the MBR back and checks that the code and the table are exactly what <see cref="WindowsMbr.Apply"/> makes of them.</summary>
    public void VerifyMbr(StorageDevice device, MediaPlan plan, CancellationToken cancellationToken)
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
    public void VerifyBootSectors(StorageDevice device, PlannedPartition main, FatBootSectors expected, CancellationToken cancellationToken)
    {
        var length = expected.SectorCount * expected.BytesPerSector;
        if (!WindowsFatBootCode.Matches(Read(device, main.StartBytes, length, cancellationToken), expected))
        {
            throw new BootrixException(ErrorCode.VerifyMismatch, "boot sectors of the main partition differ after writing") { Arguments = [main.StartBytes] };
        }
    }

    public void VerifyPartition(StorageDevice device, PlannedPartition partition, byte[] expected, CancellationToken cancellationToken)
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
