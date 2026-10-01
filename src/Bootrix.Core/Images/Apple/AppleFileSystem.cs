// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

public enum AppleFileSystem
{
    Unknown,

    /// <summary>Classic HFS (Mac OS 9 and earlier), also the wrapper around embedded HFS+.</summary>
    Hfs,
    HfsPlus,

    /// <summary>HFSX, the case-sensitive variant of HFS+.</summary>
    HfsX,
    Apfs,
}
