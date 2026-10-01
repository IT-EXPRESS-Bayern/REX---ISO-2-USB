// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.FileSystems.Ext;

internal static class ExtAssert
{
    /// <summary>
    /// e2fsck must find nothing wrong, and the superblock's free counts must equal the group totals.
    /// e2fsck -n does not fail on superblock totals alone, so that comparison is made from the dumpe2fs listing.
    /// </summary>
    public static void Clean(string image, string? context = null)
    {
        var fsck = ExtTools.Fsck(image);
        Assert.True(fsck.ExitCode == 0, $"{context} {fsck.All}");

        var dump = ExtDump.Read(image);
        Assert.Equal(dump.GroupFreeBlocks, long.Parse(dump.Header["Free blocks"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(dump.GroupFreeInodes, long.Parse(dump.Header["Free inodes"], System.Globalization.CultureInfo.InvariantCulture));
    }
}
