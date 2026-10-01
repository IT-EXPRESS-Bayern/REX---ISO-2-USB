// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

internal static class ExtFeatures
{
    public const uint CompatHasJournal = 0x4;
    public const uint CompatExtAttr = 0x8;
    public const uint CompatResizeInode = 0x10;
    public const uint CompatDirIndex = 0x20;

    public const uint IncompatFileType = 0x2;
    public const uint IncompatRecover = 0x4;
    public const uint IncompatExtents = 0x40;

    public const uint RoCompatSparseSuper = 0x1;
    public const uint RoCompatLargeFile = 0x2;
    public const uint RoCompatHugeFile = 0x8;
    public const uint RoCompatGroupChecksums = 0x10;
    public const uint RoCompatDirNlink = 0x20;
    public const uint RoCompatExtraIsize = 0x40;

    // The writer can only keep a file system consistent when it knows every layout-relevant feature.
    public const uint SupportedCompat = CompatHasJournal | CompatExtAttr | CompatResizeInode | CompatDirIndex;

    public const uint SupportedIncompat = IncompatFileType | IncompatExtents;

    public const uint SupportedRoCompat = RoCompatSparseSuper | RoCompatLargeFile | RoCompatHugeFile
        | RoCompatGroupChecksums | RoCompatDirNlink | RoCompatExtraIsize;
}
