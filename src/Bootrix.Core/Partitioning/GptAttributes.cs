// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>The 64 attribute bits of a GPT partition entry: bits 0 to 2 are defined by UEFI, 48 to 63 belong to the partition type.</summary>
[Flags]
public enum GptAttributes : ulong
{
    None = 0,

    /// <summary>The platform needs this partition to function; tools must not delete it.</summary>
    RequiredPartition = 1UL << 0,

    /// <summary>Firmware must not create a Block IO handle for the partition.</summary>
    NoBlockIoProtocol = 1UL << 1,

    /// <summary>Boot code on the protective MBR may treat this partition as bootable; UEFI ignores it.</summary>
    LegacyBiosBootable = 1UL << 2,

    /// <summary>Basic data partitions only: the volume is mounted read-only.</summary>
    ReadOnly = 1UL << 60,

    ShadowCopy = 1UL << 61,

    /// <summary>Basic data partitions only: no mount point, no volume GUID path.</summary>
    Hidden = 1UL << 62,

    /// <summary>Basic data partitions only: no drive letter is assigned automatically.</summary>
    NoDriveLetter = 1UL << 63,
}
