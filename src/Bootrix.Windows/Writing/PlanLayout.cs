// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Windows.Storage;

namespace Bootrix.Windows.Writing;

/// <summary>Translates a <see cref="MediaPlan"/> into what the platform layer applies to a disk.</summary>
public static class PlanLayout
{
    public static LayoutSpec ToLayoutSpec(MediaPlan plan, uint mbrSignature, Guid diskId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var style = plan.Scheme switch
        {
            PartitionScheme.Mbr => LayoutStyle.Mbr,
            PartitionScheme.Gpt => LayoutStyle.Gpt,
            _ => throw new InvalidOperationException("The plan creates no partition table; it cannot be applied as a layout."),
        };

        return new LayoutSpec
        {
            Style = style,
            DiskSizeBytes = plan.DeviceBytes,
            SectorSize = plan.SectorSize,
            MbrSignature = mbrSignature,
            GptDiskId = diskId,
            Partitions =
            [
                .. plan.Partitions.Select(p => new PartitionSpec
                {
                    OffsetBytes = p.StartBytes,
                    LengthBytes = p.LengthBytes,
                    MbrType = p.MbrType,
                    GptType = p.GptType,
                    Active = p.Active,
                    Name = p.Label ?? "",
                    GptAttributes = (ulong)p.GptAttributes,
                }),
            ],
        };
    }

    /// <summary>
    /// FAT partitions are formatted by Bootrix itself, before Windows sees the new layout: any size, any
    /// cluster size, the geometry of the plan. <paramref name="customize"/> lets a writer add boot code or
    /// change a field for one partition.
    /// </summary>
    public static IReadOnlyList<PartitionPayload> FatPayloads(MediaPlan plan, Func<PlannedPartition, FatFormatOptions, FatFormatOptions>? customize = null)
    {
        var payloads = new List<PartitionPayload>();
        for (var index = 0; index < plan.Partitions.Count; index++)
        {
            var partition = plan.Partitions[index];
            if (partition.FileSystem is not (FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32))
            {
                continue;
            }

            var options = plan.ToFatOptions(partition);
            options = customize?.Invoke(partition, options) ?? options;
            payloads.Add(new PartitionPayload(index, stream => FatFormatter.Format(stream, options)));
        }

        return payloads;
    }

    /// <summary>Partitions that carry a file system and therefore get a volume that Windows mounts.</summary>
    public static IReadOnlySet<int> MountedPartitions(MediaPlan plan) =>
        plan.Partitions.Select((p, i) => (p, i)).Where(x => x.p.FileSystem is not null).Select(x => x.i).ToHashSet();

    /// <summary>File systems that only Windows can create, after the volume exists.</summary>
    public static bool NeedsWindowsFormat(PlannedPartition partition) =>
        partition.FileSystem is FileSystemKind.Ntfs or FileSystemKind.ExFat or FileSystemKind.Udf or FileSystemKind.ReFs;
}
