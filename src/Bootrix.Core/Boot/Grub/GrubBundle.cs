// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Model;

namespace Bootrix.Core.Boot.Grub;

/// <summary>
/// The GRUB 2 images for BIOS booting as Bootrix ships them (see assets/third-party/SOURCES.md and
/// tools/bootcode/grub/build-core.sh). The core image carries all the modules a distribution menu asks for, so it
/// never depends on the modules of the image's own GRUB; it differs per partition table only in the root it is
/// built for: partition 1 of the boot disk on MBR, partition 2 on GPT (partition 1 is the BIOS boot partition).
/// </summary>
public sealed class GrubBundle
{
    private const string ResourcePrefix = "Bootrix.Core.Boot.Grub.2.12/";

    private GrubBundle(byte[] bootImage, byte[] coreMbr, byte[] coreGpt)
    {
        BootImage = bootImage;
        CoreMbr = coreMbr;
        CoreGpt = coreGpt;
    }

    public static GrubBundle Default { get; } = Load();

    public const string Version = "2.12";

    /// <summary>boot.img: the 512-byte sector for the start of the disk, before it is told where the core image lies.</summary>
    public ReadOnlyMemory<byte> BootImage { get; }

    public ReadOnlyMemory<byte> CoreMbr { get; }

    public ReadOnlyMemory<byte> CoreGpt { get; }

    public ReadOnlyMemory<byte> CoreFor(PartitionScheme scheme) => scheme == PartitionScheme.Gpt ? CoreGpt : CoreMbr;

    private static GrubBundle Load()
    {
        var assembly = typeof(GrubBundle).Assembly;
        return new GrubBundle(
            SyslinuxBundle.ReadResource(assembly, ResourcePrefix + "boot.img"),
            SyslinuxBundle.ReadResource(assembly, ResourcePrefix + "core-msdos.img"),
            SyslinuxBundle.ReadResource(assembly, ResourcePrefix + "core-gpt.img"));
    }
}
