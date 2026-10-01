// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Writing.Restore;

/// <summary>What a restored drive looks like: one partition over the whole disk.</summary>
public sealed record RestoreOptions
{
    /// <summary>MBR or GPT; <see cref="PartitionScheme.Auto"/> takes MBR as long as the disk is addressable with it.</summary>
    public PartitionScheme Scheme { get; init; } = PartitionScheme.Mbr;

    /// <summary>FAT32, exFAT or NTFS; <see cref="FileSystemKind.Auto"/> picks by size.</summary>
    public FileSystemKind FileSystem { get; init; } = FileSystemKind.Auto;

    public string? Label { get; init; }

    public int? ClusterSizeBytes { get; init; }
}

/// <summary>
/// Plans the clean state a stick returns to after a byte-for-byte image: a single data partition spanning the disk. The
/// partitioning and file system rules are the planner's, the same ones every other medium follows, so what a restored
/// stick gets is what a freshly formatted data stick gets.
/// </summary>
public static class RestorePlanner
{
    public static MediaPlan Plan(RestoreOptions options, DeviceCaps device)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(device);

        var target = new TargetOptions
        {
            Scheme = options.Scheme,
            FileSystem = options.FileSystem,
            Label = options.Label,
            ClusterSizeBytes = options.ClusterSizeBytes,
        };

        // No image, no boot code: the partition is plain data and nothing asks the BIOS to start it.
        var plan = LayoutPlanner.Plan(new ImageProfile { Kind = ImageKind.Unknown }, target, device);
        return plan with { Partitions = [.. plan.Partitions.Select(partition => partition with { Active = false })] };
    }
}
