// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

public enum ExtFileSystemType
{
    /// <summary>No journal.</summary>
    Ext2,

    /// <summary>Journal with block-mapped files; the default for live-USB persistence.</summary>
    Ext3,

    /// <summary>ext3 feature set plus extents, huge_file, dir_nlink and extra_isize. No flex_bg, no 64bit, no metadata checksums.</summary>
    Ext4,
}
