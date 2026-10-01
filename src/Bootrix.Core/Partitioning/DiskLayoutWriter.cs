// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Partitioning;

/// <summary>
/// Writes a <see cref="DiskLayout"/> into a stream by hand. That is what image files and tests
/// need; on a real disk Windows' own partitioning IOCTLs do the same job and this class only
/// serves layouts they cannot express (hybrid MBR, bootstrap code).
/// </summary>
public static class DiskLayoutWriter
{
    public static void WriteToStream(Stream disk, DiskLayout layout, int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(layout);
        if (disk.Length < layout.TotalSectors * sectorSize)
        {
            throw new ArgumentException("The stream is shorter than the layout's disk.", nameof(disk));
        }

        switch (layout.Scheme)
        {
            case PartitionScheme.Mbr:
                WriteMbr(disk, layout, sectorSize);
                break;
            case PartitionScheme.Gpt:
                WriteGpt(disk, layout, sectorSize);
                break;
            default:
                throw new ArgumentException("Choose Mbr or Gpt before writing a layout.", nameof(layout));
        }

        disk.Flush();
    }

    private static void WriteMbr(Stream disk, DiskLayout layout, int sectorSize)
    {
        var builder = new MbrBuilder().WithSignature(layout.MbrSignature).WithGeometry(layout.Geometry);
        if (layout.Bootstrap is not null)
        {
            builder.WithBootstrap(layout.Bootstrap);
        }

        foreach (var partition in layout.Partitions)
        {
            builder.AddPartition(partition.MbrType, partition.StartLba, partition.SectorCount, partition.Active);
        }

        WriteSector0(disk, builder.Build(), sectorSize);
    }

    private static void WriteGpt(Stream disk, DiskLayout layout, int sectorSize)
    {
        var builder = new GptBuilder(layout.TotalSectors, sectorSize).WithDiskGuid(layout.DiskGuid);
        foreach (var partition in layout.Partitions)
        {
            builder.AddPartition(
                partition.GptType, partition.StartLba, partition.EndLba, partition.Name, partition.Attributes, partition.UniqueId);
        }

        var mbr = layout.HybridPartitions.Count > 0
            ? HybridMbr.Create(
                [.. layout.HybridPartitions.Select(index => Mirror(layout.Partitions[index]))],
                builder.FirstUsableLba,
                bootstrap: layout.Bootstrap,
                diskSignature: layout.MbrSignature,
                geometry: layout.Geometry)
            : MbrBuilder.Protective(layout.TotalSectors, layout.Geometry) with { Bootstrap = Pad(layout.Bootstrap) };

        builder.WithMbr(mbr).Build().WriteTo(disk);
    }

    private static HybridEntry Mirror(PartitionEntry partition) =>
        new(partition.MbrType, partition.StartLba, partition.SectorCount, partition.Active);

    private static byte[] Pad(byte[]? bootstrap)
    {
        var padded = new byte[Mbr.BootstrapLength];
        bootstrap?.CopyTo(padded, 0);
        return padded;
    }

    private static void WriteSector0(Stream disk, Mbr mbr, int sectorSize)
    {
        var sector = new byte[sectorSize];
        mbr.WriteTo(sector);
        disk.Position = 0;
        disk.Write(sector, 0, sector.Length);
    }
}
