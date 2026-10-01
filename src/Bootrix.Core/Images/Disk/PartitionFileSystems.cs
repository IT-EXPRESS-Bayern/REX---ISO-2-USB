// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Disk;

/// <summary>Opens the FAT volumes inside a disk image: the EFI system partition, a Windows setup partition, a Raspberry Pi boot partition.</summary>
internal static class PartitionFileSystems
{
    private const int MaxVolumes = 4;

    public static List<ImageFileSystem> Open(Stream disk, DiskLayout layout, int entryLimit, CancellationToken cancellationToken)
    {
        var volumes = new List<ImageFileSystem>();
        foreach (var (offset, length) in Regions(layout))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (volumes.Count == MaxVolumes)
            {
                break;
            }

            if (ImageFileSystem.OpenFatRegion(disk, offset, length, entryLimit, cancellationToken) is { } volume)
            {
                volumes.Add(volume);
            }
        }

        return volumes;
    }

    /// <summary>Byte ranges of the partitions; extended containers are skipped because their logical partitions are listed in a chain of EBRs.</summary>
    private static IEnumerable<(long Offset, long Length)> Regions(DiskLayout layout)
    {
        foreach (var partition in layout.MbrPartitions.Where(p => !p.IsProtective && p.Type is not (0x05 or 0x0F or 0x85)))
        {
            yield return (partition.StartSector * 512, partition.SectorCount * 512);
        }

        foreach (var partition in layout.GptPartitions)
        {
            yield return (partition.FirstSector * layout.GptSectorSize, (partition.LastSector - partition.FirstSector + 1) * layout.GptSectorSize);
        }
    }
}
