// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

/// <summary>Entries of the root directory that belong to the volume the image was taken from, not to the image.</summary>
internal static class MediaExclusions
{
    private static readonly HashSet<string> RootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information",
        "$RECYCLE.BIN",
        "RECYCLER",
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "IndexerVolumeGuid",
        "WPSettings.dat",
    };

    public static bool IsExcluded(string path)
    {
        var slash = path.IndexOf('/', StringComparison.Ordinal);
        return RootNames.Contains(slash < 0 ? path : path[..slash]);
    }
}
