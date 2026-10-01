// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Images.Policy;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Linux;

/// <summary>Turns a media plan and what the image's file tree offers into the settings of the media builder.</summary>
public static class LinuxBuildPlanner
{
    private const int ExFatLabelLength = 15;

    public static LinuxBuildSettings Create(MediaPlan plan, ImageProfile image, LinuxTreeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(facts);

        var traits = ImagePolicy.Default.TraitsOf(image.Family);
        var main = plan.Partitions.FirstOrDefault(p => p.Role == PartitionRole.Main);
        return new LinuxBuildSettings
        {
            Bios = BiosBootChooser.Decide(plan, facts),
            Uefi = plan.Firmware is TargetFirmware.Uefi or TargetFirmware.BiosAndUefi,
            MediumLabel = main is null ? null : LabelOnMedium(main),
            Family = image.Family,
            Traits = traits,
            Persistence = plan.NeedsPersistencePartition,
        };
    }

    /// <summary>The label the file system of the partition ends up with; FAT folds and shortens what it is given, so boot configurations have to name the folded form.</summary>
    public static string? LabelOnMedium(PlannedPartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (string.IsNullOrWhiteSpace(partition.Label))
        {
            return null;
        }

        return partition.FileSystem switch
        {
            FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32 => FileSystems.Fat.FatLabel.Normalize(partition.Label),
            FileSystemKind.ExFat => partition.Label.Length > ExFatLabelLength ? partition.Label[..ExFatLabelLength] : partition.Label,
            _ => partition.Label,
        };
    }
}
