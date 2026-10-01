// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Model;

/// <summary>Where the first partition starts when old-BIOS compatibility is requested.</summary>
public enum LegacyPartitionStart
{
    /// <summary>64 KiB, which is LBA 128 on 512-byte sectors; what Rufus uses for its old-BIOS fixes.</summary>
    Kib64,

    /// <summary>LBA 63, the cylinder-aligned start of DOS FDISK and every pre-Vista Windows setup.</summary>
    Lba63,
}
