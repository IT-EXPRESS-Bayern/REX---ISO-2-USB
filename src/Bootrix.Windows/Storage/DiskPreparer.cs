// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Storage;

/// <summary>Content that is written into a partition before Windows gets to see it, typically a freshly formatted FAT file system.</summary>
public sealed record PartitionPayload(int PartitionIndex, Action<Stream> Write);

public sealed record PrepareRequest(
    StorageDevice Device,
    LayoutSpec Layout,
    IReadOnlyList<PartitionPayload> Payloads,
    IReadOnlySet<int> MountedPartitions);

public sealed record PreparedPartition(int Index, long OffsetBytes, VolumeInfo? Volume);

public sealed record PreparedDisk(IReadOnlyList<PreparedPartition> Partitions);

/// <summary>
/// Turns a disk into the requested layout. Order matters: all old volumes are locked first,
/// the old tables are destroyed, file systems are written into the future partitions while no
/// volume exists there yet, and only then the new layout is announced to Windows. That way the
/// shell never sees an unformatted partition and has no reason to ask the user to format it.
/// </summary>
public sealed class DiskPreparer(ILogger<DiskPreparer>? logger = null)
{
    private static readonly TimeSpan VolumeTimeout = TimeSpan.FromSeconds(45);

    private readonly ILogger _log = logger ?? NullLogger<DiskPreparer>.Instance;

    public async Task<PreparedDisk> PrepareAsync(PrepareRequest request, CancellationToken cancellationToken = default)
    {
        var device = request.Device;
        if (device.IsBlocked)
        {
            throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
        }

        _log.LogInformation("Preparing disk {Disk} ({Name}), {Count} partition(s)", device.DiskNumber, device.DisplayName, request.Layout.Partitions.Count);

        using (var locks = VolumeLockSet.Acquire(device, _log, cancellationToken))
        using (var disk = DiskAccess.Open(device, write: true, cancellationToken))
        {
            _log.LogInformation("Locked {Volumes} volume(s)", locks.VolumeCount);

            DiskWiper.WipeTables(disk);
            WritePayloads(disk, request);
            ApplyLayout(disk, request.Layout);
            VerifyLayout(disk, request.Layout);
        }

        var prepared = new List<PreparedPartition>();
        for (var i = 0; i < request.Layout.Partitions.Count; i++)
        {
            var offset = request.Layout.Partitions[i].OffsetBytes;
            VolumeInfo? volume = null;
            if (request.MountedPartitions.Contains(i))
            {
                volume = await VolumeArrivalWaiter.WaitAsync(device.DiskNumber, offset, VolumeTimeout, cancellationToken).ConfigureAwait(false);
                _log.LogInformation("Partition {Index} mounted as {Volume}", i, volume.VolumeGuidPath);
            }

            prepared.Add(new PreparedPartition(i, offset, volume));
        }

        return new PreparedDisk(prepared);
    }

    private static void WritePayloads(PhysicalDisk disk, PrepareRequest request)
    {
        foreach (var payload in request.Payloads)
        {
            var partition = request.Layout.Partitions[payload.PartitionIndex];
            using var stream = new BlockDeviceStream(disk, partition.OffsetBytes, partition.LengthBytes);
            payload.Write(stream);
            stream.Flush();
        }
    }

    private void ApplyLayout(PhysicalDisk disk, LayoutSpec layout)
    {
        // CREATE_DISK alone does not reset a disk completely; a RAW round trip first avoids half-applied tables.
        Control(disk, Ioctl.DiskCreateDisk, DriveLayoutBuilder.BuildCreateRawDisk(), "CREATE_DISK (raw)");
        Control(disk, Ioctl.DiskUpdateProperties, [], "UPDATE_PROPERTIES");
        Control(disk, Ioctl.DiskCreateDisk, DriveLayoutBuilder.BuildCreateDisk(layout), "CREATE_DISK");
        Control(disk, Ioctl.DiskUpdateProperties, [], "UPDATE_PROPERTIES");
        Control(disk, Ioctl.DiskSetDriveLayoutEx, DriveLayoutBuilder.BuildLayout(layout), "SET_DRIVE_LAYOUT_EX");
        Control(disk, Ioctl.DiskUpdateProperties, [], "UPDATE_PROPERTIES");
        _log.LogInformation("Applied {Style} layout", layout.Style);
    }

    private static void Control(PhysicalDisk disk, uint code, byte[] input, string what)
    {
        if (!DeviceIo.TryControl(disk.Handle, code, input, [], out _, out var error))
        {
            throw new BootrixException(ErrorCode.LayoutRejected, $"{what} failed with Win32 error {error}");
        }
    }

    /// <summary>Reads the table back; Windows may silently reduce a layout, for example on removable media of older builds.</summary>
    private static void VerifyLayout(PhysicalDisk disk, LayoutSpec layout)
    {
        var actual = DriveLayoutReader.Read(disk.Handle);
        foreach (var expected in layout.Partitions)
        {
            if (!actual.Partitions.Any(p => p.Offset == expected.OffsetBytes && p.Length == expected.LengthBytes))
            {
                throw new BootrixException(ErrorCode.LayoutRejected, $"partition at {expected.OffsetBytes} is missing after applying the layout");
            }
        }
    }
}
