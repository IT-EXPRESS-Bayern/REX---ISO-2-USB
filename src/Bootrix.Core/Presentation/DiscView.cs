// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;
using Bootrix.Core.Optical;
using Bootrix.Core.Text;

namespace Bootrix.Core.Presentation;

/// <summary>A drive and its disc as one list entry.</summary>
public sealed record DriveDescription(string Title, string Details, bool CanWrite, bool HasDisc);

public static class DiscView
{
    public static DriveDescription Describe(OpticalDrive drive, OpticalMedia media, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(localizer);

        var title = string.IsNullOrWhiteSpace(drive.DisplayName) ? drive.Id : drive.DisplayName;
        var details = new List<string>();
        if (!drive.CanRecord)
        {
            details.Add(localizer.Get("Disc.Drive.Reader"));
        }

        details.Add(MediaText(media, localizer));
        return new DriveDescription(title, string.Join("  ·  ", details), drive.CanRecord, media.IsPresent);
    }

    public static string MediaText(OpticalMedia media, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(localizer);

        if (media.Condition == OpticalMediaCondition.NoMedia)
        {
            return localizer.Get("Disc.Media.None");
        }

        var parts = new List<string>();
        if (TypeName(media.Type) is { } type)
        {
            parts.Add(type);
        }

        parts.Add(localizer.Get("Disc.Media." + media.Condition));
        if (media.Condition is OpticalMediaCondition.Blank or OpticalMediaCondition.Appendable && media.FreeBytes > 0)
        {
            parts.Add(localizer.Get("Disc.Media.Free", ByteSize.Format(media.FreeBytes, localizer.Culture)));
        }

        return string.Join(", ", parts);
    }

    /// <summary>The name printed on the disc; null for a type the drive could not identify.</summary>
    public static string? TypeName(OpticalMediaType type) => type switch
    {
        OpticalMediaType.CdRom => "CD-ROM",
        OpticalMediaType.CdR => "CD-R",
        OpticalMediaType.CdRw => "CD-RW",
        OpticalMediaType.DvdRom => "DVD-ROM",
        OpticalMediaType.DvdRam => "DVD-RAM",
        OpticalMediaType.DvdPlusR => "DVD+R",
        OpticalMediaType.DvdPlusRw => "DVD+RW",
        OpticalMediaType.DvdPlusRDualLayer => "DVD+R DL",
        OpticalMediaType.DvdMinusR => "DVD-R",
        OpticalMediaType.DvdMinusRw => "DVD-RW",
        OpticalMediaType.DvdMinusRDualLayer => "DVD-R DL",
        OpticalMediaType.DvdPlusRwDualLayer => "DVD+RW DL",
        OpticalMediaType.HdDvdRom => "HD DVD-ROM",
        OpticalMediaType.HdDvdR => "HD DVD-R",
        OpticalMediaType.HdDvdRam => "HD DVD-RAM",
        OpticalMediaType.BdRom => "BD-ROM",
        OpticalMediaType.BdR => "BD-R",
        OpticalMediaType.BdRe => "BD-RE",
        _ => null,
    };
}
