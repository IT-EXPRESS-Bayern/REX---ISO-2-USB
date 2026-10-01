// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

public class LayoutPlannerWindowsTests
{
    private static MediaPlan Plan(ImageProfile image, TargetOptions? target = null, DeviceCaps? device = null) =>
        LayoutPlanner.Plan(image, target ?? new TargetOptions(), device ?? Stick(64 * Gib));

    [Fact]
    public void UefiOnly_SmallWim_IsOneFat32PartitionOnGpt()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Uefi });

        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Equal(WriteMethod.ExtractFiles, plan.WriteMethod);
        Assert.Equal(BootMethod.UefiNative, plan.BootMethod);
        Assert.False(plan.UsesUefiNtfs);
        Assert.False(plan.SplitWim);
        var main = Assert.Single(plan.Partitions);
        Assert.Equal(FileSystemKind.Fat32, main.FileSystem);
        Assert.Equal(GptTypes.BasicData, main.GptType);
        Assert.Equal(Mib, main.StartBytes);
        Assert.Equal("CCCOMA_X64FRE_DE-DE_DV9", main.Label);
    }

    [Fact]
    public void UefiOnly_WimOverFourGibibytes_IsSplitOnFat32ByDefault()
    {
        var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { Firmware = TargetFirmware.Uefi });

        Assert.Equal(FileSystemKind.Fat32, plan.Partitions.Single().FileSystem);
        Assert.True(plan.SplitWim);
        Assert.False(plan.UsesUefiNtfs);
        Assert.Equal(BootMethod.UefiNative, plan.BootMethod);
    }

    [Fact]
    public void UefiOnly_WimOverFourGibibytes_OnNtfsGetsTheUefiNtfsPartition()
    {
        var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { Firmware = TargetFirmware.Uefi, FileSystem = FileSystemKind.Ntfs });

        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.True(plan.UsesUefiNtfs);
        Assert.False(plan.SplitWim);
        Assert.Equal(BootMethod.UefiNtfs, plan.BootMethod);
        Assert.Equal(2, plan.Partitions.Count);

        var main = plan.Partitions[0];
        var helper = plan.Partitions[1];
        Assert.Equal(PartitionRole.Main, main.Role);
        Assert.Equal(FileSystemKind.Ntfs, main.FileSystem);
        Assert.Equal(GptTypes.BasicData, main.GptType);
        Assert.Equal(PartitionRole.UefiNtfs, helper.Role);
        Assert.Equal(Mib, helper.LengthBytes);
        Assert.Equal(GptTypes.BasicData, helper.GptType);
        Assert.Equal(GptAttributes.NoDriveLetter, helper.GptAttributes);
        Assert.Equal(MbrPartitionType.EfiSystem, helper.MbrType);
        Assert.Equal(main.EndBytes, helper.StartBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.UefiNtfsCa2011);
    }

    [Fact]
    public void BiosOnly_IsAnActiveFat32PartitionOnMbr()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Bios });

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal(BootMethod.WindowsBootmgrBios, plan.BootMethod);
        var main = Assert.Single(plan.Partitions);
        Assert.True(main.Active);
        Assert.Equal(MbrPartitionType.Fat32Lba, main.MbrType);
        Assert.Equal(FileSystemKind.Fat32, main.FileSystem);
    }

    [Fact]
    public void BiosOnly_WithNtfs_NeedsNoUefiHelper()
    {
        var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { Firmware = TargetFirmware.Bios, FileSystem = FileSystemKind.Ntfs });

        var main = Assert.Single(plan.Partitions);
        Assert.Equal(MbrPartitionType.Ntfs, main.MbrType);
        Assert.True(main.Active);
        Assert.False(plan.UsesUefiNtfs);
        Assert.Equal(BootMethod.WindowsBootmgrBios, plan.BootMethod);
    }

    [Fact]
    public void BiosAndUefi_IsTheAutomaticChoice_AndStaysOnOneActiveMbrPartition()
    {
        var plan = Plan(WindowsIso());

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal(TargetFirmware.BiosAndUefi, plan.Firmware);
        Assert.Equal(BootMethod.WindowsBootmgrBios | BootMethod.UefiNative, plan.BootMethod);
        Assert.True(plan.Partitions.Single().Active);
    }

    [Fact]
    public void BiosAndUefi_WithNtfs_AddsTheEfiPartitionBehindTheData()
    {
        var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { FileSystem = FileSystemKind.Ntfs });

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal(BootMethod.WindowsBootmgrBios | BootMethod.UefiNtfs, plan.BootMethod);
        Assert.Equal(MbrPartitionType.Ntfs, plan.Partitions[0].MbrType);
        Assert.True(plan.Partitions[0].Active);
        Assert.Equal(MbrPartitionType.EfiSystem, plan.Partitions[1].MbrType);
        Assert.False(plan.Partitions[1].Active);
    }

    [Fact]
    public void ExFat_IsAllowedAndUsesTheUefiHelper()
    {
        var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { FileSystem = FileSystemKind.ExFat });

        Assert.Equal(FileSystemKind.ExFat, plan.Partitions[0].FileSystem);
        Assert.Equal(MbrPartitionType.Ntfs, plan.Partitions[0].MbrType);
        Assert.True(plan.UsesUefiNtfs);
    }

    [Theory]
    [InlineData(FileSystemKind.Udf)]
    [InlineData(FileSystemKind.ReFs)]
    [InlineData(FileSystemKind.Ext3)]
    [InlineData(FileSystemKind.Fat16)]
    public void UnsuitableFileSystems_AreRefusedForWindowsSetup(FileSystemKind fileSystem)
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(WindowsIso(), new TargetOptions { FileSystem = fileSystem }));

        Assert.Equal(ErrorCode.FileSystemUnsupported, ex.Code);
    }

    [Fact]
    public void Arm64_HasNoBiosAndSaysSoWhenAskedForOne()
    {
        var auto = Plan(WindowsIso(arch: WindowsArch.Arm64));
        Assert.Equal(TargetFirmware.Uefi, auto.Firmware);
        Assert.Equal(PartitionScheme.Gpt, auto.Scheme);
        Assert.Equal(BootMethod.UefiNative, auto.BootMethod);

        var forced = Plan(WindowsIso(arch: WindowsArch.Arm64), new TargetOptions { Firmware = TargetFirmware.BiosAndUefi });
        Assert.Equal(TargetFirmware.Uefi, forced.Firmware);
        Assert.Contains(forced.Warnings, w => w.Code == PlanWarningCodes.ArmHasNoBios);
    }

    [Fact]
    public void ExplicitGpt_WithBios_WarnsThatFewBiosesBootIt()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Gpt });

        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.BiosOnGpt);
    }

    [Fact]
    public void ExplicitMbr_WithUefiOnly_IsKept()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Uefi, Scheme = PartitionScheme.Mbr });

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.False(plan.Partitions.Single().Active);
    }

    [Fact]
    public void ImageWithoutEfiFiles_WarnsWhenUefiIsRequested()
    {
        var image = WindowsIso() with { HasEfiBootFiles = false };

        var plan = Plan(image, new TargetOptions { Firmware = TargetFirmware.Uefi });

        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.NoEfiBootFiles);
    }

    [Fact]
    public void Partitions_AreMebibyteAlignedAndFillTheDevice()
    {
        var plan = Plan(WindowsIso(), device: Stick(32_000_000_000));

        var main = plan.Partitions.Single();
        Assert.Equal(Mib, main.StartBytes);
        Assert.Equal(0, main.LengthBytes % Mib);
        Assert.True(main.EndBytes <= 32_000_000_000);
        Assert.True(32_000_000_000 - main.EndBytes < 2 * Mib);
    }

    [Fact]
    public void Gpt_LeavesRoomForTheBackupTable()
    {
        var device = Stick(32_000_000_000);
        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Uefi }, device);

        var lastUsable = (device.SizeBytes / 512 - 34) * 512;
        Assert.True(plan.Partitions.Single().EndBytes <= lastUsable);
    }

    [Fact]
    public void WindowsToGo_PutsEspAndMsrInFrontOfNtfsOnGpt()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { WindowsToGo = true, Firmware = TargetFirmware.Uefi }, Stick(64 * Gib));

        Assert.Equal(WriteMethod.ApplyImage, plan.WriteMethod);
        Assert.True(plan.WindowsToGo);
        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Equal(
            [PartitionRole.Esp, PartitionRole.Msr, PartitionRole.Main],
            plan.Partitions.Select(p => p.Role));
        Assert.Equal(300 * Mib, plan.Partitions[0].LengthBytes);
        Assert.Equal(GptTypes.EfiSystem, plan.Partitions[0].GptType);
        Assert.Equal(16 * Mib, plan.Partitions[1].LengthBytes);
        Assert.Equal(GptTypes.MicrosoftReserved, plan.Partitions[1].GptType);
        Assert.Equal(FileSystemKind.Ntfs, plan.Partitions[2].FileSystem);
        Assert.Equal(BootMethod.UefiNative, plan.BootMethod);
    }

    [Fact]
    public void WindowsToGo_OnMbr_HasAnActiveSystemPartitionAndNoMsr()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { WindowsToGo = true }, Stick(64 * Gib));

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal([PartitionRole.Esp, PartitionRole.Main], plan.Partitions.Select(p => p.Role));
        Assert.True(plan.Partitions[0].Active);
        Assert.False(plan.Partitions[1].Active);
        Assert.Equal(BootMethod.WindowsBootmgrBios | BootMethod.UefiNative, plan.BootMethod);
    }

    [Fact]
    public void WindowsToGo_NeedsAWindowsImageAndAnNtfsVolume()
    {
        Assert.Throws<BootrixException>(() => Plan(LinuxIso(), new TargetOptions { WindowsToGo = true }));
        var ex = Assert.Throws<BootrixException>(() =>
            Plan(WindowsIso(), new TargetOptions { WindowsToGo = true, FileSystem = FileSystemKind.Fat32 }));
        Assert.Equal(ErrorCode.FileSystemUnsupported, ex.Code);
    }

    [Fact]
    public void WindowsToGo_OnATinyDevice_IsTooSmall()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            Plan(WindowsIso(), new TargetOptions { WindowsToGo = true }, Stick(8 * Gib)));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
    }

    [Fact]
    public void EspAndMsr_AreOnlyCreatedForWindowsToGo()
    {
        foreach (var firmware in new[] { TargetFirmware.Auto, TargetFirmware.Uefi, TargetFirmware.Bios, TargetFirmware.BiosAndUefi })
        {
            var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { Firmware = firmware, FileSystem = FileSystemKind.Ntfs });

            Assert.DoesNotContain(plan.Partitions, p => p.Role is PartitionRole.Esp or PartitionRole.Msr);
        }
    }
}
