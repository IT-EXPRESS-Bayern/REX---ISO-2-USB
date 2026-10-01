// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Wim;

namespace Bootrix.Core.Writing.Windows.Customization;

internal static class BootImageIndex
{
    /// <summary>The image that runs Windows Setup. In Microsoft's boot.wim it is the second one; the first is a bare Windows PE.</summary>
    public const int StandardSetupIndex = 2;

    /// <summary>
    /// Looks for the image Microsoft names "Microsoft Windows Setup" and otherwise falls back to the standard position,
    /// which is what every original boot.wim has. Null when the file has fewer images than that.
    /// </summary>
    public static int? FindSetup(WimMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var named = metadata.Editions.FirstOrDefault(e => e.Name?.StartsWith("Microsoft Windows Setup", StringComparison.OrdinalIgnoreCase) == true);
        if (named is not null)
        {
            return named.Index;
        }

        return metadata.Editions.Any(e => e.Index == StandardSetupIndex) ? StandardSetupIndex : null;
    }

    public static int? FindSetup(string wimPath)
    {
        using var stream = new FileStream(wimPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FindSetup(WimMetadata.Read(stream));
    }
}
