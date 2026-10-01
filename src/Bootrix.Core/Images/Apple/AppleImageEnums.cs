// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

/// <summary>How the image file stores its volume.</summary>
public enum AppleImageContainer
{
    /// <summary>The file is the volume itself (.cdr, .iso, .toast, raw .dmg, .img).</summary>
    Raw,
    Udif,
    SparseImage,
    SparseBundle,
}

public enum AppleImageScheme
{
    None,
    Apm,
    Gpt,
    Mbr,
}

public enum AppleImageKind
{
    /// <summary>Nothing Mac specific: an ordinary ISO, a PC disk image or unknown data.</summary>
    NotApple,

    /// <summary>A classic HFS volume without partition table.</summary>
    HfsVolume,

    /// <summary>An HFS+ or HFSX volume without partition table.</summary>
    HfsPlusVolume,
    ApfsContainer,

    /// <summary>A disk with an Apple Partition Map.</summary>
    ApplePartitionMap,

    /// <summary>A GPT disk with Apple partition types.</summary>
    GptMacDisk,

    /// <summary>An MBR disk with HFS partitions, as some Intel Mac recovery sticks are laid out.</summary>
    MbrMacDisk,

    /// <summary>A disc that is ISO 9660 and HFS at once.</summary>
    HybridDisc,

    /// <summary>A PC ISO (isohybrid) that also carries an Apple partition map.</summary>
    IsoHybridWithApm,
}
