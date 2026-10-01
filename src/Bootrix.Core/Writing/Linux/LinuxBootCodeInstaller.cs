// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Grub;
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Errors;
using Bootrix.Core.IO;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Linux;

/// <summary>
/// Puts the BIOS boot code of the chosen loader on a disk whose partitions already hold the copied files.
/// Works on streams so that a disk image under test and a physical disk with locked volumes take the same path.
/// </summary>
public static class LinuxBootCodeInstaller
{
    /// <param name="disk">The whole disk, readable and writable, addressed in bytes from its first sector.</param>
    /// <param name="plan">The plan the disk was prepared with; it says where the partitions are.</param>
    /// <param name="decision">What <see cref="BiosBootChooser"/> decided for this image.</param>
    /// <exception cref="BootrixException">The loader cannot be installed on this disk.</exception>
    public static void Install(Stream disk, MediaPlan plan, BiosBootDecision decision)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(decision);

        switch (decision.Loader)
        {
            case BiosLoader.Syslinux:
                InstallSyslinux(disk, plan, decision);
                break;
            case BiosLoader.Grub:
                GrubBiosInstaller.Install(
                    disk,
                    GrubBundle.Default,
                    new GrubInstallRequest(plan.Scheme, decision.GrubEmbedStartSector, decision.GrubEmbedSectors));
                break;
        }
    }

    private static void InstallSyslinux(Stream disk, MediaPlan plan, BiosBootDecision decision)
    {
        var main = plan.Partitions.FirstOrDefault(p => p.Role == PartitionRole.Main)
            ?? throw new BootrixException(ErrorCode.BootloaderInstallFailed, "the plan has no main partition") { Arguments = ["Syslinux", "no main partition"] };
        var choice = decision.Syslinux ?? throw new InvalidOperationException("The decision names no Syslinux release.");

        using (var volume = new StreamSlice(disk, main.StartBytes, main.LengthBytes))
        {
            SyslinuxInstaller.Install(volume, choice.Bundle, new SyslinuxInstallOptions { SingleSectorReads = plan.LegacyBios });
        }

        // "mbr_f" insists on drive 0x80, which old BIOSes that report another number need.
        SyslinuxMbr.Write(disk, plan.Scheme == PartitionScheme.Gpt, forceDrive80: plan.LegacyBios);
    }
}
