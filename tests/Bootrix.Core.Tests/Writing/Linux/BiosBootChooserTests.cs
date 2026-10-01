// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Writing.Linux;

namespace Bootrix.Core.Tests.Writing.Linux;

public class BiosBootChooserTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static readonly SyslinuxVersion Debian = new(6, 4, "20190206");

    private static ImageProfile Image(string family, bool big = false) => new()
    {
        Kind = ImageKind.LinuxIsoOnly,
        Family = family,
        Container = ImageContainer.Iso9660,
        VolumeLabel = "LIVE",
        TotalBytes = big ? 7 * Gib : 2 * Gib,
        LargestFileBytes = big ? 5 * Gib : Gib,
        HasBiosBootFiles = true,
        HasEfiBootFiles = true,
        HasElToritoBios = true,
    };

    private static MediaPlan Plan(string family, TargetOptions? target = null, bool big = false, long device = 16 * Gib, int sector = 512) =>
        LayoutPlanner.Plan(Image(family, big), target ?? new TargetOptions { Mode = WriteMode.Extract }, new DeviceCaps { SizeBytes = device, LogicalSectorSize = sector });

    private static LinuxTreeFacts Facts(SyslinuxVersion? version = null, bool syslinux = true, bool grub = true, bool grub2 = false) => new()
    {
        SyslinuxConfig = syslinux ? "isolinux/isolinux.cfg" : null,
        IsolinuxBinary = syslinux ? "isolinux/isolinux.bin" : null,
        IsolinuxVersion = syslinux ? version ?? Debian : null,
        HasGrubConfig = grub,
        Grub2Config = grub2 ? "boot/grub2/grub.cfg" : null,
    };

    [Fact]
    public void Debian_WithMatchingSyslinuxAndAGrubMenu_KeepsSyslinux()
    {
        var decision = BiosBootChooser.Decide(Plan("debian-live"), Facts());

        Assert.Equal(BiosLoader.Syslinux, decision.Loader);
        Assert.Equal("6.04-pre1", decision.Syslinux!.Bundle.Id);
        Assert.Equal(SyslinuxMatch.SameRelease, decision.Syslinux.Match);
        Assert.Equal(LinuxNoticeKeys.SyslinuxVersionDiffers, Assert.Single(decision.Notices).Key);
        Assert.Equal(WarningSeverity.Info, decision.Notices[0].Severity);
    }

    [Fact]
    public void ExactRelease_ReportsTheInstalledOne()
    {
        var decision = BiosBootChooser.Decide(Plan("debian-live"), Facts(new SyslinuxVersion(6, 3, "2014-10-06")));

        Assert.Equal("6.03", decision.Syslinux!.Bundle.Id);
        Assert.Equal(LinuxNoticeKeys.SyslinuxInstalled, decision.Notices[0].Key);
    }

    [Fact]
    public void OlderGeneration_WithAGrubMenu_SwitchesToGrub()
    {
        var decision = BiosBootChooser.Decide(Plan("debian-live"), Facts(new SyslinuxVersion(4, 5, "2011-12-09")));

        Assert.Equal(BiosLoader.Grub, decision.Loader);
        Assert.Equal(1, decision.GrubEmbedStartSector);
        Assert.Equal(2047, decision.GrubEmbedSectors);
        Assert.Contains(decision.Notices, n => n.Key == LinuxNoticeKeys.GrubInsteadOfSyslinuxVersion);
    }

    [Fact]
    public void OlderGeneration_WithoutAGrubMenu_StaysWithSyslinuxAndWarns()
    {
        var decision = BiosBootChooser.Decide(Plan("debian-live"), Facts(new SyslinuxVersion(4, 5, "2011-12-09"), grub: false));

        Assert.Equal(BiosLoader.Syslinux, decision.Loader);
        Assert.Equal(SyslinuxMatch.Different, decision.Syslinux!.Match);
        Assert.Equal(WarningSeverity.Warning, decision.Notices[0].Severity);
    }

    [Fact]
    public void Ubuntu_PlannedForGrub_UsesGrub()
    {
        var plan = Plan("ubuntu");

        var decision = BiosBootChooser.Decide(plan, Facts());

        Assert.True(plan.BootMethod.HasFlag(BootMethod.Grub));
        Assert.Equal(BiosLoader.Grub, decision.Loader);
        Assert.DoesNotContain(decision.Notices, n => n.Key == LinuxNoticeKeys.GrubInsteadOfSyslinuxVersion);
    }

    [Fact]
    public void Ubuntu_WithOnlyAnIsolinuxMenu_FallsBackToSyslinux()
    {
        var decision = BiosBootChooser.Decide(Plan("ubuntu"), Facts(grub: false));

        Assert.Equal(BiosLoader.Syslinux, decision.Loader);
    }

    [Fact]
    public void Ntfs_CannotCarrySyslinux_SoGrubTakesOver()
    {
        var plan = Plan("debian-live", big: true);

        var decision = BiosBootChooser.Decide(plan, Facts());

        Assert.Equal(FileSystemKind.Ntfs, plan.Partitions.Single(p => p.Role == PartitionRole.Main).FileSystem);
        Assert.Equal(BiosLoader.Grub, decision.Loader);
        Assert.Contains(decision.Notices, n => n.Key == LinuxNoticeKeys.GrubInsteadOfSyslinuxFileSystem);
    }

    [Fact]
    public void Ntfs_WithoutAGrubMenu_HasNoBiosLoader()
    {
        var decision = BiosBootChooser.Decide(Plan("debian-live", big: true), Facts(grub: false));

        Assert.Equal(BiosLoader.None, decision.Loader);
        Assert.Equal(LinuxNoticeKeys.NoBiosLoader, decision.Notices[0].Key);
        Assert.Equal(WarningSeverity.Warning, decision.Notices[0].Severity);
    }

    [Fact]
    public void FourKnDevice_HasNoBiosLoader()
    {
        var decision = BiosBootChooser.Decide(Plan("debian-live", device: 2048 * Gib / 64, sector: 4096), Facts());

        Assert.Equal(BiosLoader.None, decision.Loader);
        Assert.Equal(LinuxNoticeKeys.BiosNeeds512ByteSectors, decision.Notices[0].Key);
    }

    [Fact]
    public void UefiOnlyMedium_NeedsNoBiosLoader()
    {
        var plan = Plan("debian-live", new TargetOptions { Mode = WriteMode.Extract, Firmware = TargetFirmware.Uefi });

        var decision = BiosBootChooser.Decide(plan, Facts());

        Assert.Equal(BiosLoader.None, decision.Loader);
        Assert.Empty(decision.Notices);
    }

    [Fact]
    public void GptWithSyslinuxPlan_HasNoRoomForGrub()
    {
        var plan = Plan("debian-live", new TargetOptions { Mode = WriteMode.Extract, Scheme = PartitionScheme.Gpt });

        var decision = BiosBootChooser.Decide(plan, Facts(new SyslinuxVersion(4, 5, "")));

        Assert.DoesNotContain(plan.Partitions, p => p.Role == PartitionRole.BiosBoot);
        Assert.Equal(BiosLoader.Syslinux, decision.Loader);
    }

    [Fact]
    public void GptWithGrubPlan_UsesTheBiosBootPartition()
    {
        var plan = Plan("ubuntu", new TargetOptions { Mode = WriteMode.Extract, Scheme = PartitionScheme.Gpt });
        var biosBoot = plan.Partitions.Single(p => p.Role == PartitionRole.BiosBoot);

        var decision = BiosBootChooser.Decide(plan, Facts());

        Assert.Equal(BiosLoader.Grub, decision.Loader);
        Assert.Equal(biosBoot.StartLba(512), decision.GrubEmbedStartSector);
        Assert.Equal(biosBoot.SectorCount(512), decision.GrubEmbedSectors);
    }

    [Fact]
    public void LegacyStartOffset_LeavesNoRoomForGrub()
    {
        var plan = Plan("debian-live", new TargetOptions { Mode = WriteMode.Extract, LegacyBiosFixes = true, LegacyStart = LegacyPartitionStart.Lba63 });

        var decision = BiosBootChooser.Decide(plan, Facts(new SyslinuxVersion(4, 5, "")));

        Assert.Equal(BiosLoader.Syslinux, decision.Loader);
    }

    [Fact]
    public void GrubMenuInBootGrub2_IsEnoughForGrub()
    {
        var decision = BiosBootChooser.Decide(Plan("debian-live"), Facts(syslinux: false, grub: false, grub2: true));

        Assert.Equal(BiosLoader.Grub, decision.Loader);
    }

    [Fact]
    public void NeitherMenu_HasNoBiosLoader()
    {
        var decision = BiosBootChooser.Decide(Plan("debian-live"), Facts(syslinux: false, grub: false));

        Assert.Equal(BiosLoader.None, decision.Loader);
    }
}
