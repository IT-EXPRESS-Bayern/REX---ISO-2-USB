// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Ext;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Writing.Raw;

/// <summary>
/// Adds the persistence partition behind an image that was written to a disk byte for byte: finds room in the
/// table the image brought along, formats the partition with ext and registers it. The file system goes first and
/// the table last, so a write that is cut short leaves the image as it was.
/// </summary>
public static class PersistenceInstaller
{
    /// <summary>Everything old file systems keep their signatures in (btrfs at 64 KiB, ext at 1 KiB, NTFS and XFS at 0) is cleared.</summary>
    private const long ClearedHeadBytes = 1024 * 1024;

    /// <exception cref="BootrixException">
    /// <see cref="ErrorCode.PersistenceLayoutUnsupported"/> when the table cannot be extended safely and
    /// <see cref="ErrorCode.PersistenceTooSmall"/> when too little room is left.
    /// </exception>
    public static PersistenceResult Install(IBlockDevice device, PersistenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(request);
        if (device.SectorSize != PersistencePlacer.SectorSize)
        {
            throw new BootrixException(ErrorCode.PersistenceLayoutUnsupported, "4Kn media")
            {
                Arguments = ["the table of the image is written for 512-byte sectors"],
            };
        }

        var totalSectors = device.Length / PersistencePlacer.SectorSize;
        PersistencePlacement placement;
        using (var tables = new BlockDeviceStream(device, 0, totalSectors * PersistencePlacer.SectorSize))
        {
            placement = PersistencePlacer.Decide(tables, totalSectors, request);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var start = placement.StartLba * PersistencePlacer.SectorSize;
        var length = placement.SectorCount * PersistencePlacer.SectorSize;
        DiskWiper.ZeroRange(device, start, Math.Min(ClearedHeadBytes, length));

        using (var partition = new BlockDeviceStream(device, start, length))
        {
            ExtFormatter.Format(
                partition,
                new ExtFormatOptions
                {
                    Type = request.FileSystem,
                    Label = request.Label,
                    SizeBytes = length,
                    RootFiles = request.Files,
                    TimeProvider = request.TimeProvider,
                },
                cancellationToken);
            partition.Flush();
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (var tables = new BlockDeviceStream(device, 0, totalSectors * PersistencePlacer.SectorSize))
        {
            Register(tables, totalSectors, placement, request);
            tables.Flush();
        }

        return new PersistenceResult(placement.Table, placement.Slot, start, length, request.Label);
    }

    /// <summary>The protective entry has to span the larger disk; boot code, disk signature and the position of the entry stay.</summary>
    internal static Mbr WidenProtectiveEntry(Mbr mbr, long totalSectors)
    {
        var protective = MbrBuilder.Protective(totalSectors).Entries[0];
        var entries = mbr.Entries
            .Select(e => e.Type == MbrPartitionType.GptProtective ? e with { SectorCount = protective.SectorCount, LastChs = protective.LastChs } : e)
            .ToArray();
        return mbr with { Entries = entries };
    }

    private static void Register(Stream disk, long totalSectors, PersistencePlacement placement, PersistenceRequest request)
    {
        var last = placement.StartLba + placement.SectorCount - 1;
        if (placement.Table == PersistenceTable.Gpt)
        {
            var entry = new GptPartition(GptTypes.LinuxData, Guid.NewGuid(), placement.StartLba, last, GptAttributes.None, request.Label);
            var mbr = WidenProtectiveEntry(placement.Mbr, totalSectors);
            placement.Gpt!.Relocate(totalSectors, mbr, [(placement.Slot, entry)]).WriteTo(disk);
            return;
        }

        var geometry = ChsGeometry.Translated;
        var slots = placement.Mbr.Entries.ToArray();
        slots[placement.Slot] = new MbrEntry(
            0,
            MbrPartitionType.Linux,
            (uint)placement.StartLba,
            (uint)placement.SectorCount,
            geometry.FromLba(placement.StartLba),
            geometry.FromLba(last));
        disk.Position = 0;
        disk.Write((placement.Mbr with { Entries = slots }).ToBytes());
    }
}
