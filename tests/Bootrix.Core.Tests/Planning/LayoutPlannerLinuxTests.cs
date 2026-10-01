// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

public class LayoutPlannerLinuxTests
{
    private static MediaPlan Plan(ImageProfile image, TargetOptions? target = null, DeviceCaps? device = null) =>
        LayoutPlanner.Plan(image, target ?? new TargetOptions(), device ?? Stick(16 * Gib));

    [Fact]
    public void HybridImage_IsCopiedRawWithoutAnyPartitions()
    {
        var plan = Plan(LinuxHybrid());

        Assert.Equal(WriteMethod.RawCopy, plan.WriteMethod);
        Assert.Equal(BootMethod.ImageNative, plan.BootMethod);
        Assert.Equal(PartitionScheme.Auto, plan.Scheme);
        Assert.Empty(plan.Partitions);
        Assert.False(plan.NeedsPersistencePartition);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.RawCopyReadOnlyMedia);
        Assert.Throws<InvalidOperationException>(() => plan.ToDiskLayout());
    }

    [Fact]
    public void HybridImage_LargerThanTheDevice_IsTooSmallWithBothSizes()
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(LinuxHybrid(total: 3 * Gib), device: Stick(2 * Gib)));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
        Assert.Equal(["3 GiB", "2 GiB"], ex.Arguments);
    }

    [Fact]
    public void HybridImage_WithPersistence_GetsAnExt3PartitionBehindTheImage()
    {
        var plan = Plan(LinuxHybrid("ubuntu", 3 * Gib + 100 * Mib), new TargetOptions { PersistenceMegabytes = 2048 });

        Assert.Equal(WriteMethod.RawCopy, plan.WriteMethod);
        Assert.True(plan.NeedsPersistencePartition);
        var persistence = Assert.Single(plan.Partitions);
        Assert.Equal(PartitionRole.Persistence, persistence.Role);
        Assert.Equal(FileSystemKind.Ext3, persistence.FileSystem);
        Assert.Equal("writable", persistence.Label);
        Assert.Equal(3 * Gib + 100 * Mib, persistence.StartBytes);
        Assert.Equal(2048 * Mib, persistence.LengthBytes);
        Assert.Equal(MbrPartitionType.Linux, persistence.MbrType);
        Assert.Equal(GptTypes.LinuxData, persistence.GptType);
        Assert.DoesNotContain(plan.Warnings, w => w.Code == PlanWarningCodes.RawCopyReadOnlyMedia);
    }

    [Fact]
    public void HybridImage_PersistenceLabel_FollowsTheDistributionFamily()
    {
        Assert.Equal("persistence", Plan(LinuxHybrid("kali"), new TargetOptions { PersistenceMegabytes = 512 }).Partitions[0].Label);
        Assert.Equal("writable", Plan(LinuxHybrid("mint"), new TargetOptions { PersistenceMegabytes = 512 }).Partitions[0].Label);
    }

    [Fact]
    public void HybridImage_PersistenceLargerThanTheFreeSpace_IsReducedWithAWarning()
    {
        var plan = Plan(LinuxHybrid(total: 3 * Gib), new TargetOptions { PersistenceMegabytes = 50_000 }, Stick(8 * Gib));

        var persistence = plan.Partitions.Single();
        Assert.Equal(3 * Gib, persistence.StartBytes);
        Assert.Equal(5 * Gib - Mib, persistence.LengthBytes);
        Assert.Equal(8 * Gib - Mib, persistence.EndBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.PersistenceReduced);
    }

    [Fact]
    public void HybridImage_PersistenceWithoutRoom_IsTooSmall()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            Plan(LinuxHybrid(total: 3 * Gib), new TargetOptions { PersistenceMegabytes = 512 }, Stick(3 * Gib + 8 * Mib)));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
    }

    [Fact]
    public void Persistence_BelowTheMinimum_IsRefused()
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(LinuxHybrid(), new TargetOptions { PersistenceMegabytes = 4 }));

        Assert.Equal(ErrorCode.PersistenceTooSmall, ex.Code);
    }

    [Fact]
    public void Persistence_ForOtherImages_IsIgnoredWithAWarning()
    {
        var raw = Plan(RawDisk(), new TargetOptions { PersistenceMegabytes = 1024 });
        var windows = Plan(WindowsIso(), new TargetOptions { PersistenceMegabytes = 1024 });

        Assert.Empty(raw.Partitions);
        Assert.Contains(raw.Warnings, w => w.Code == PlanWarningCodes.PersistenceUnsupported);
        Assert.False(windows.NeedsPersistencePartition);
        Assert.Contains(windows.Warnings, w => w.Code == PlanWarningCodes.PersistenceUnsupported);
    }

    [Fact]
    public void HybridImage_InIsoMode_IsExtractedOntoFat32()
    {
        var plan = Plan(LinuxHybrid(), new TargetOptions { Mode = WriteMode.Extract });

        Assert.Equal(WriteMethod.ExtractFiles, plan.WriteMethod);
        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal(BootMethod.SyslinuxMbr | BootMethod.UefiNative, plan.BootMethod);
        var main = Assert.Single(plan.Partitions);
        Assert.Equal(FileSystemKind.Fat32, main.FileSystem);
        Assert.True(main.Active);
        Assert.Equal("DEBIAN_LIVE", main.Label);
    }

    [Fact]
    public void IsoOnlyImage_IsExtractedWithSyslinuxForBiosAndNativeUefi()
    {
        var plan = Plan(LinuxIso());

        Assert.Equal(WriteMethod.ExtractFiles, plan.WriteMethod);
        Assert.Equal(BootMethod.SyslinuxMbr | BootMethod.UefiNative, plan.BootMethod);
        Assert.Equal(MbrPartitionType.Fat32Lba, plan.Partitions.Single().MbrType);
    }

    [Fact]
    public void UefiOnlyIsoMode_UsesGptWithAFat32DataPartition()
    {
        var plan = Plan(LinuxIso(), new TargetOptions { Firmware = TargetFirmware.Uefi });

        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Equal(BootMethod.UefiNative, plan.BootMethod);
        var main = Assert.Single(plan.Partitions);
        Assert.Equal(GptTypes.BasicData, main.GptType);
        Assert.Equal(GptAttributes.None, main.GptAttributes);
    }

    [Fact]
    public void UbuntuFamily_BootsBiosThroughGrub()
    {
        var plan = Plan(LinuxIso("ubuntu"));

        Assert.Equal(BootMethod.Grub | BootMethod.UefiNative, plan.BootMethod);
    }

    [Fact]
    public void Grub_OnGpt_GetsABiosBootPartitionBeforeTheData()
    {
        var plan = Plan(LinuxIso("ubuntu"), new TargetOptions { Scheme = PartitionScheme.Gpt });

        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Equal([PartitionRole.BiosBoot, PartitionRole.Main], plan.Partitions.Select(p => p.Role));
        var gap = plan.Partitions[0];
        Assert.Equal(GptTypes.BiosBoot, gap.GptType);
        Assert.Equal(Mib, gap.StartBytes);
        Assert.Equal(Mib, gap.LengthBytes);
        Assert.Null(gap.FileSystem);
        Assert.Equal(gap.EndBytes, plan.Partitions[1].StartBytes);
        Assert.DoesNotContain(plan.Warnings, w => w.Code == PlanWarningCodes.BiosOnGpt);
    }

    [Fact]
    public void Syslinux_OnGpt_MarksThePartitionLegacyBiosBootable()
    {
        var plan = Plan(LinuxIso(), new TargetOptions { Scheme = PartitionScheme.Gpt });

        Assert.Equal(GptAttributes.LegacyBiosBootable, plan.Partitions.Single().GptAttributes);
        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
    }

    [Fact]
    public void Grub_WithClassicOffset_MovesTheFirstPartitionBehindTheBootloaderGap()
    {
        var plan = Plan(LinuxIso("ubuntu"), new TargetOptions { LegacyBiosFixes = true, LegacyStart = LegacyPartitionStart.Lba63 });

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal(Mib, plan.Partitions[0].StartBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.GrubNeedsGap);
    }

    [Fact]
    public void Syslinux_WithClassicOffset_KeepsLba63()
    {
        var plan = Plan(LinuxIso(), new TargetOptions { LegacyBiosFixes = true, LegacyStart = LegacyPartitionStart.Lba63 });

        Assert.Equal(63 * 512, plan.Partitions[0].StartBytes);
        Assert.DoesNotContain(plan.Warnings, w => w.Code == PlanWarningCodes.GrubNeedsGap);
    }

    [Fact]
    public void IsoMode_Persistence_SitsAtTheEndOfTheDevice()
    {
        var plan = Plan(LinuxIso(total: 1 * Gib), new TargetOptions { PersistenceMegabytes = 4096 }, Stick(16 * Gib));

        Assert.True(plan.NeedsPersistencePartition);
        Assert.Equal([PartitionRole.Main, PartitionRole.Persistence], plan.Partitions.Select(p => p.Role));
        var persistence = plan.Partitions[1];
        Assert.Equal(4096 * Mib, persistence.LengthBytes);
        Assert.Equal("persistence", persistence.Label);
        Assert.Equal(plan.Partitions[0].EndBytes, persistence.StartBytes);
        Assert.True(16 * Gib - persistence.EndBytes < 2 * Mib);
    }

    [Fact]
    public void IsoMode_PersistenceBeyondTheFreeSpace_IsReducedToWhatFitsNextToTheImage()
    {
        var plan = Plan(LinuxIso(total: 1 * Gib), new TargetOptions { PersistenceMegabytes = 100_000 }, Stick(4 * Gib));

        var main = plan.Partitions[0];
        var persistence = plan.Partitions[1];
        Assert.True(main.LengthBytes >= 1 * Gib + 32 * Mib);
        Assert.True(persistence.LengthBytes < 100_000 * Mib);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.PersistenceReduced);
    }

    [Fact]
    public void IsoMode_PersistenceWithNoRoomLeft_IsTooSmall()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            Plan(LinuxIso(total: 1 * Gib), new TargetOptions { PersistenceMegabytes = 512 }, Stick(1 * Gib + 40 * Mib)));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
    }

    [Fact]
    public void IsoMode_FileOverFourGibibytes_FallsBackToNtfsWithTheUefiHelper()
    {
        var plan = Plan(LinuxIso(big: true), device: Stick(32 * Gib));

        Assert.Equal(FileSystemKind.Ntfs, plan.Partitions[0].FileSystem);
        Assert.True(plan.UsesUefiNtfs);
        Assert.Equal(BootMethod.SyslinuxMbr | BootMethod.UefiNtfs, plan.BootMethod);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.LinuxNtfsSupport);
        Assert.False(plan.SplitWim);
    }

    [Fact]
    public void IsoMode_ExplicitFat32_WithAHugeFile_IsRefused()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            Plan(LinuxIso(big: true), new TargetOptions { FileSystem = FileSystemKind.Fat32 }, Stick(32 * Gib)));

        Assert.Equal(ErrorCode.FileTooLargeForFileSystem, ex.Code);
        Assert.Equal("5 GiB", ex.Arguments[1]);
    }

    [Fact]
    public void IsoMode_ExFatOnUbuntu_WarnsAboutCasper()
    {
        var plan = Plan(LinuxIso("ubuntu"), new TargetOptions { FileSystem = FileSystemKind.ExFat });

        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.ExFatCasper);
        Assert.True(plan.UsesUefiNtfs);
    }

    [Fact]
    public void RawDisk_AndBsdAndApple_AreAlwaysCopiedRaw()
    {
        foreach (var kind in new[] { ImageKind.RawDisk, ImageKind.Bsd, ImageKind.Apple })
        {
            var plan = Plan(new ImageProfile { Kind = kind, TotalBytes = 1 * Gib });

            Assert.Equal(WriteMethod.RawCopy, plan.WriteMethod);
            Assert.Equal(BootMethod.ImageNative, plan.BootMethod);
            Assert.Empty(plan.Partitions);
        }
    }

    [Fact]
    public void RawDisk_CannotBeExtracted()
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(RawDisk(), new TargetOptions { Mode = WriteMode.Extract }));

        Assert.Equal(ErrorCode.WriteModeUnsupported, ex.Code);
    }

    [Fact]
    public void WindowsIso_CannotBeCopiedRaw()
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(WindowsIso(), new TargetOptions { Mode = WriteMode.RawCopy }));

        Assert.Equal(ErrorCode.WriteModeUnsupported, ex.Code);
    }

    [Fact]
    public void HybridWindowsStyleImage_CanBeCopiedRawOnRequest()
    {
        var image = WindowsIso() with { IsHybrid = true };

        var plan = Plan(image, new TargetOptions { Mode = WriteMode.RawCopy });

        Assert.Equal(WriteMethod.RawCopy, plan.WriteMethod);
    }

    [Fact]
    public void RawCopy_WarnsWhenTheRequestedFirmwareHasNoBootFiles()
    {
        var image = RawDisk() with { HasEfiBootFiles = false };

        var plan = Plan(image, new TargetOptions { Firmware = TargetFirmware.Uefi });

        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.NoEfiBootFiles);
    }

    [Fact]
    public void RawCopy_WithoutKnownSize_SkipsTheSizeCheck()
    {
        var plan = Plan(new ImageProfile { Kind = ImageKind.RawDisk }, device: Stick(8 * Mib));

        Assert.Equal(WriteMethod.RawCopy, plan.WriteMethod);
    }
}
