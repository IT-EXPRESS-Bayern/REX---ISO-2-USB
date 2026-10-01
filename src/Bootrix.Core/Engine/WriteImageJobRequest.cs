// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Engine;

/// <summary>
/// Writes an image to one or several disks the way the plan for that image decides: byte for byte,
/// or partitioned, formatted and filled with the files of the image. Replaces the raw request for
/// everything except the plain "write this image as it is" case, which stays available.
/// </summary>
public sealed record WriteImageJobRequest : EngineJobRequest
{
    public required string ImagePath { get; init; }

    public required IReadOnlyList<EngineTarget> Targets { get; init; }

    /// <summary>Layout, boot, setup and verification options; <see cref="JobSpec.Source"/> is not used, the image path is given above.</summary>
    public JobSpec Spec { get; init; } = new();

    /// <summary>Clear-text password for the local account of the answer file. Never part of a profile or a log.</summary>
    public string? LocalAccountPassword { get; init; }
}
