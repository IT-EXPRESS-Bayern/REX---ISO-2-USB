// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Wim;
using Bootrix.Core.Localization;
using Bootrix.Core.Text;
using Bootrix.Core.Tiny;

namespace Bootrix.Core.Presentation;

/// <summary>An option group of a Tiny profile as the user sees it: what it is, what it does, and whether it is on unless switched off.</summary>
public sealed record TinyGroupOption(string Id, string Title, string Hint, bool DefaultOn);

public static class TinyView
{
    /// <summary>
    /// Shown as the single "skip the hardware check" switch instead of as a group of its own,
    /// because the request has a field for it that also covers the boot image.
    /// </summary>
    public const string HardwareBypassGroup = "hardware-bypass";

    public static string ProfileName(string profileId, Localizer localizer) => localizer.Get("Tiny.Profile." + profileId);

    public static string ProfileDescription(string profileId, Localizer localizer) => localizer.Get("Tiny.Profile." + profileId + ".Desc");

    public static IReadOnlyList<TinyGroupOption> Groups(TinyProfile profile, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(localizer);

        return
        [
            .. profile.Groups
                .Where(group => !string.Equals(group.Id, HardwareBypassGroup, StringComparison.OrdinalIgnoreCase))
                .Select(group => new TinyGroupOption(
                    group.Id,
                    localizer.Get("Tiny.Group." + group.Id),
                    localizer.Get("Tiny.Group." + group.Id + ".Hint"),
                    group.Default)),
        ];
    }

    public static string EditionLabel(WimEdition edition, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(edition);
        ArgumentNullException.ThrowIfNull(localizer);

        var name = edition.Name ?? edition.ProductName ?? edition.EditionId ?? edition.Index.ToString(localizer.Culture);
        var details = new List<string>();
        if (edition.Arch != Images.WindowsArch.Unknown)
        {
            details.Add(edition.Arch.ToString());
        }

        if (edition.TotalBytes > 0)
        {
            details.Add(ByteSize.Format(edition.TotalBytes, localizer.Culture));
        }

        return details.Count == 0 ? name : $"{name} ({string.Join(", ", details)})";
    }
}
