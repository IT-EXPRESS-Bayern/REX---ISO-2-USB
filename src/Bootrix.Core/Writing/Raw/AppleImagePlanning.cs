// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Images.Apple;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Raw;

/// <summary>The inspection with Apple findings folded in, and the notes for the user that go with it.</summary>
public sealed record AppleRefinement(ImageInspection Inspection, IReadOnlyList<PlanWarning> Warnings)
{
    public MediaPlan Apply(MediaPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Warnings.Count == 0 ? plan : plan with { Warnings = [.. plan.Warnings, .. Warnings.Where(w => plan.Warnings.All(existing => existing.Code != w.Code))] };
    }
}

/// <summary>
/// What an image of Mac origin needs on top of the general inspection. The inspector sees an Apple container only from
/// outside (a .dmg is "a file with a trailer"), and a bare HFS+ volume as data to be extracted. The classifier looks
/// at the volume: Mac images are always copied raw, and their boot limits are told as warnings, never as errors,
/// because the stick may well be wanted for a Mac that does boot from it.
/// </summary>
public static class AppleImagePlanning
{
    private static readonly AppleImageKind[] MacKinds =
    [
        AppleImageKind.HfsVolume,
        AppleImageKind.HfsPlusVolume,
        AppleImageKind.ApfsContainer,
        AppleImageKind.ApplePartitionMap,
        AppleImageKind.GptMacDisk,
        AppleImageKind.MbrMacDisk,
        AppleImageKind.HybridDisc,
    ];

    /// <summary>
    /// Classifies the volume behind <paramref name="volume"/> when there is reason to think it is an Apple image: it came out of an
    /// Apple container, carries an Apple partition map or was recognised as Apple already. Anything else comes back as it is.
    /// </summary>
    /// <param name="volume">The decoded volume or the image file itself; has to be seekable.</param>
    /// <param name="fromAppleContainer">The stream was unpacked from a .dmg, .sparseimage or .sparsebundle.</param>
    public static AppleRefinement Refine(ImageInspection inspection, Stream volume, bool fromAppleContainer)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(volume);

        var suspect = fromAppleContainer || inspection.Profile.Kind == ImageKind.Apple || inspection.Layout?.HasApm == true;
        if (!suspect || !volume.CanSeek || inspection.Compression != CompressionFormat.None)
        {
            return new AppleRefinement(inspection, []);
        }

        var info = AppleImageClassifier.Classify(volume);
        var profile = inspection.Profile;
        if (Array.IndexOf(MacKinds, info.Kind) >= 0 && profile.Kind != ImageKind.Apple)
        {
            profile = profile with { Kind = ImageKind.Apple };
        }

        // A Mac image is copied raw, so what lands on the device is the whole volume, whatever the file tree adds up to.
        if (profile.Kind == ImageKind.Apple)
        {
            profile = profile with { TotalBytes = Math.Max(profile.TotalBytes, volume.Length), ImageBytes = volume.Length };
        }

        return new AppleRefinement(
            inspection with { Profile = profile },
            [.. info.Hints.Select(hint => new PlanWarning(hint.ResourceKey()))]);
    }
}
