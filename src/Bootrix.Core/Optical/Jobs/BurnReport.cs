// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Jobs;

public sealed record DriveBurnResult(OpticalDrive Drive, bool Succeeded, Exception? Error, TimeSpan Duration, bool ReadBackVerified = false);

/// <param name="SourceSha256">SHA-256 of the image as burned (including the padding to whole sectors); only known when the read-back check was requested.</param>
public sealed record BurnReport(IReadOnlyList<DriveBurnResult> Drives, string? SourceSha256)
{
    public bool AllSucceeded => Drives.All(d => d.Succeeded);
}
