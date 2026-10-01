// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Boot.Syslinux;

/// <summary>
/// The 440 bytes of Syslinux boot code that go in front of the partition table: <c>mbr.bin</c> starts the active
/// MBR partition, <c>gptmbr.bin</c> the GPT partition that carries the "legacy BIOS bootable" attribute, and
/// <c>mbr_f.bin</c> insists on drive 0x80 for BIOSes that hand over a wrong drive number.
/// </summary>
public static class SyslinuxMbr
{
    public const int CodeLength = 440;

    private const string ResourcePrefix = "Bootrix.Core.Boot.Syslinux.mbr/";

    public static byte[] Code(bool gpt, bool forceDrive80 = false)
    {
        var name = gpt ? "gptmbr.bin" : forceDrive80 ? "mbr_f.bin" : "mbr.bin";
        return SyslinuxBundle.ReadResource(typeof(SyslinuxMbr).Assembly, ResourcePrefix + name);
    }

    /// <summary>Replaces the boot code of the first sector and leaves the disk signature and the partition table alone.</summary>
    public static void Write(Stream disk, bool gpt, bool forceDrive80 = false)
    {
        ArgumentNullException.ThrowIfNull(disk);
        var code = Code(gpt, forceDrive80);
        if (code.Length != CodeLength)
        {
            throw new BootrixException(ErrorCode.BootloaderInstallFailed, $"unexpected MBR code length {code.Length}") { Arguments = ["Syslinux", "MBR code"] };
        }

        var sector = new byte[512];
        disk.Position = 0;
        disk.ReadExactly(sector);
        code.CopyTo(sector, 0);
        disk.Position = 0;
        disk.Write(sector);
        disk.Flush();
    }
}
