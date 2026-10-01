// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Dos;

/// <summary>What the DOS and format-only writers decide from a plan: whether they apply and how the system is assembled.</summary>
public static class DosMedium
{
    /// <summary>A DOS stick or diskette: nothing is copied from the image, the system comes from Bootrix (FreeDOS) or from Microsoft (MS-DOS).</summary>
    public static bool IsDosMedium(MediaPlan plan, ImageProfile image)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(image);
        return plan.WriteMethod == WriteMethod.FormatOnly && image.Kind == ImageKind.Dos;
    }

    /// <summary>Partition and format only: a data stick without anything on it, or a formatted diskette.</summary>
    public static bool IsPlainFormat(MediaPlan plan, ImageProfile image)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(image);
        return plan.WriteMethod == WriteMethod.FormatOnly && image.Kind == ImageKind.Unknown;
    }

    /// <summary>A volume in one of the diskette formats at LBA 0, as opposed to a stick that merely has no partition table.</summary>
    public static bool IsDiskette(MediaPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Superfloppy && plan.Partitions is [{ } only] && FloppyPreset.FromSize(only.LengthBytes) is not null;
    }

    /// <summary>
    /// The system for <paramref name="plan"/>. <paramref name="diskcopyDll"/> is needed for MS-DOS only and is the verified file
    /// from <see cref="MsDosFetcher"/>.
    /// </summary>
    public static DosSystem CreateSystem(DosOptions options, MediaPlan plan, byte[]? diskcopyDll)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);

        if (options.Flavor == DosFlavor.FreeDos)
        {
            // An old BIOS may not offer the LBA extensions, so the kernel keeps CHS where the partition type allows it.
            return FreeDosSystem.Create(options.Kernel, IsDiskette(plan), forceLba: !plan.LegacyBios);
        }

        if (diskcopyDll is null)
        {
            throw new BootrixException(ErrorCode.MsDosNotDownloaded, "diskcopy.dll was not supplied");
        }

        EnsureMsDosFits(plan);
        return MsDosFloppy.ToSystem(DiskcopyDll.FindFloppyImage(diskcopyDll));
    }

    /// <summary>MS-DOS here boots from the FAT12 boot sector of its own diskette; a FAT32 plan has to be refused before anything is written.</summary>
    public static void EnsureMsDosFits(MediaPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Partitions.Any(p => p.FileSystem == FileSystemKind.Fat32))
        {
            throw new BootrixException(ErrorCode.FileSystemUnsupported, "MS-DOS boots from FAT12 and FAT16 only")
            {
                Arguments = [FileSystemKind.Fat32, "MS-DOS"],
            };
        }
    }
}
