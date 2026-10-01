// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Writing.Linux;

/// <summary>
/// The small FAT partition behind a Linux stick whose main partition is NTFS or exFAT: it holds the UEFI:NTFS loader,
/// which brings its own file system driver and starts \EFI\BOOT\bootx64.efi from the main partition, because UEFI
/// firmware cannot read either file system itself.
/// </summary>
public static class UefiNtfsHelper
{
    public const int ImageBytes = 1024 * 1024;

    private const string Resource = "Bootrix.Core.Writing.Linux.uefi-ntfs.img";

    public static byte[] Image() => SyslinuxBundle.ReadResource(typeof(UefiNtfsHelper).Assembly, Resource);

    /// <summary>Writes the image to the start of a stream that spans the helper partition.</summary>
    /// <exception cref="BootrixException">The partition is smaller than the image.</exception>
    public static void Write(Stream partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        var image = Image();
        if (partition.Length < image.Length)
        {
            var detail = $"the UEFI:NTFS partition holds {partition.Length} bytes, the image needs {image.Length}";
            throw new BootrixException(ErrorCode.BootloaderInstallFailed, detail) { Arguments = ["UEFI:NTFS", detail] };
        }

        partition.Position = 0;
        partition.Write(image);
        partition.Flush();
    }
}
