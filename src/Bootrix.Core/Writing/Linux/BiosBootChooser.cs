// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Grub;
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Linux;

public enum BiosLoader
{
    None,
    Syslinux,
    Grub,
}

/// <param name="Syslinux">The release to install when <paramref name="Loader"/> is Syslinux.</param>
/// <param name="GrubEmbedStartSector">Where GRUB's core image goes when <paramref name="Loader"/> is GRUB.</param>
/// <param name="GrubEmbedSectors">How many sectors are free there.</param>
/// <param name="Notices">What the person should be told about the decision.</param>
public sealed record BiosBootDecision(
    BiosLoader Loader,
    SyslinuxChoice? Syslinux,
    long GrubEmbedStartSector,
    long GrubEmbedSectors,
    IReadOnlyList<ImageWarning> Notices);

/// <summary>
/// Chooses the BIOS boot loader for a Linux medium. The planner names the loader it prefers; this checks it against
/// what the image brings (isolinux version, GRUB menu) and what the medium can do (Syslinux needs FAT and 512-byte
/// sectors, GRUB needs room in front of the first partition) and falls back to the other one when that is better.
/// </summary>
public static class BiosBootChooser
{
    public static BiosBootDecision Decide(MediaPlan plan, LinuxTreeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(facts);

        var notices = new List<ImageWarning>();
        if (plan.Firmware is not (TargetFirmware.Bios or TargetFirmware.BiosAndUefi))
        {
            return None(notices);
        }

        if (plan.SectorSize != 512)
        {
            notices.Add(new ImageWarning(LinuxNoticeKeys.BiosNeeds512ByteSectors));
            return None(notices);
        }

        var main = plan.Partitions.FirstOrDefault(p => p.Role == PartitionRole.Main);
        var onFat = main?.FileSystem is FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32;
        var syslinuxChoice = SyslinuxBundle.Select(facts.IsolinuxVersion);

        var canSyslinux = onFat && facts.SyslinuxConfig is not null;
        var grubHasMenu = facts.HasGrubConfig || facts.Grub2Config is not null;
        var (grubStart, grubSectors) = GrubSpace(plan);
        var coreSectors = (GrubBundle.Default.CoreFor(plan.Scheme).Length + 511) / 512;
        var canGrub = grubHasMenu && grubSectors >= coreSectors;

        // Syslinux stays unless GRUB is what the planner asked for, or the image's Syslinux is of another generation
        // than anything shipped (then its modules would run on a core that does not belong to them).
        var wantsGrub = plan.BootMethod.HasFlag(BootMethod.Grub);
        var versionOff = syslinuxChoice.Match is SyslinuxMatch.Different && facts.IsolinuxVersion is not null;

        BiosLoader loader;
        if (canSyslinux && canGrub)
        {
            loader = wantsGrub || versionOff ? BiosLoader.Grub : BiosLoader.Syslinux;
        }
        else if (canSyslinux || canGrub)
        {
            loader = canSyslinux ? BiosLoader.Syslinux : BiosLoader.Grub;
        }
        else
        {
            notices.Add(new ImageWarning(LinuxNoticeKeys.NoBiosLoader, WarningSeverity.Warning, main?.FileSystem?.ToString() ?? "-"));
            return None(notices);
        }

        if (loader == BiosLoader.Grub)
        {
            if (!onFat && plan.BootMethod.HasFlag(BootMethod.SyslinuxMbr))
            {
                notices.Add(new ImageWarning(LinuxNoticeKeys.GrubInsteadOfSyslinuxFileSystem, WarningSeverity.Info, main?.FileSystem?.ToString() ?? "-"));
            }
            else if (versionOff && !wantsGrub)
            {
                notices.Add(new ImageWarning(LinuxNoticeKeys.GrubInsteadOfSyslinuxVersion, WarningSeverity.Info, facts.IsolinuxVersion!.Value.ToString()));
            }

            notices.Add(new ImageWarning(LinuxNoticeKeys.GrubInstalled, WarningSeverity.Info, GrubBundle.Version));
            return new BiosBootDecision(BiosLoader.Grub, null, grubStart, grubSectors, notices);
        }

        var found = facts.IsolinuxVersion?.ToString() ?? "-";
        notices.Add(new ImageWarning(
            syslinuxChoice.Match is SyslinuxMatch.Exact ? LinuxNoticeKeys.SyslinuxInstalled : LinuxNoticeKeys.SyslinuxVersionDiffers,
            syslinuxChoice.Match is SyslinuxMatch.Exact or SyslinuxMatch.SameRelease ? WarningSeverity.Info : WarningSeverity.Warning,
            syslinuxChoice.Match is SyslinuxMatch.Exact ? syslinuxChoice.Bundle.Id : found,
            syslinuxChoice.Match is SyslinuxMatch.Exact ? found : syslinuxChoice.Bundle.Id));
        return new BiosBootDecision(BiosLoader.Syslinux, syslinuxChoice, 0, 0, notices);
    }

    /// <summary>Where GRUB's core image could go: sector 1 in front of the first MBR partition, or the BIOS boot partition on GPT.</summary>
    private static (long Start, long Sectors) GrubSpace(MediaPlan plan)
    {
        if (plan.Scheme == PartitionScheme.Gpt)
        {
            var biosBoot = plan.Partitions.FirstOrDefault(p => p.Role == PartitionRole.BiosBoot);
            return biosBoot is null ? (0, 0) : (biosBoot.StartLba(plan.SectorSize), biosBoot.SectorCount(plan.SectorSize));
        }

        if (plan.Scheme != PartitionScheme.Mbr || plan.Partitions.Count == 0)
        {
            return (0, 0);
        }

        return (1, plan.Partitions.Min(p => p.StartLba(plan.SectorSize)) - 1);
    }

    private static BiosBootDecision None(List<ImageWarning> notices) => new(BiosLoader.None, null, 0, 0, notices);
}
