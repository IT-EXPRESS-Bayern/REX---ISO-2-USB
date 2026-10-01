// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Raw;
using Bootrix.Windows.Storage;

namespace Bootrix.Windows.Writing;

/// <summary>One disk of a write job: the device the user confirmed, its fingerprint and the plan made for exactly this device.</summary>
public sealed class MediaWriteTarget
{
    public required StorageDevice Device { get; init; }

    public required DiskIdentity Identity { get; init; }

    public required MediaPlan Plan { get; init; }

    /// <summary>Filled in when the disk has been partitioned: where each partition ended up and which volume Windows mounted for it.</summary>
    public PreparedDisk? Prepared { get; set; }
}

/// <summary>Everything the steps of a writer need to know about the job. Steps of one writer share its state through the targets.</summary>
public sealed class MediaWriteContext
{
    public required string JobId { get; init; }

    public required string ImagePath { get; init; }

    public required ImageInspection Inspection { get; init; }

    public required JobSpec Spec { get; init; }

    public required IReadOnlyList<MediaWriteTarget> Targets { get; init; }

    /// <summary>Scratch folder for this job; deleted when the job ends.</summary>
    public required string WorkDirectory { get; init; }

    public string? LocalAccountPassword { get; init; }

    /// <summary>The file inside a zip archive that holds the image; null lets the archive's image be chosen.</summary>
    public string? ArchiveEntry { get; init; }

    /// <summary>What a raw write does with a .bmap file next to the image.</summary>
    public BlockMapUse BlockMap { get; init; } = BlockMapUse.Auto;

    public ImageProfile Image => Inspection.Profile;

    public MediaPlan Plan => Targets[0].Plan;
}
