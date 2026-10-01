// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

public class LayoutPlannerDataAndDosTests
{
    private static MediaPlan Plan(ImageProfile image, TargetOptions? target = null, DeviceCaps? device = null) =>
        LayoutPlanner.Plan(image, target ?? new TargetOptions(), device ?? Stick(8 * Gib));

    [Fact]
    public void DataImage_OnASmallStick_IsExtractedOntoFat32WithoutBootFlags()
    {
        var plan = Plan(Data());

        Assert.Equal(WriteMethod.ExtractFiles, plan.WriteMethod);
        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal(BootMethod.None, plan.BootMethod);
        Assert.Equal(TargetFirmware.Auto, plan.Firmware);
        var main = Assert.Single(plan.Partitions);
        Assert.Equal(FileSystemKind.Fat32, main.FileSystem);
        Assert.Equal(MbrPartitionType.Fat32Lba, main.MbrType);
        Assert.False(main.Active);
    }

    [Fact]
    public void DataImage_WithAFileOverFourGibibytes_GetsExFat()
    {
        var plan = Plan(Data(big: true), device: Stick(16 * Gib));

        var main = Assert.Single(plan.Partitions);
        Assert.Equal(FileSystemKind.ExFat, main.FileSystem);
        Assert.Equal(MbrPartitionType.Ntfs, main.MbrType);
        Assert.False(plan.UsesUefiNtfs);
    }

    [Fact]
    public void DataImage_OnAStickAboveThirtyTwoGibibytes_GetsExFat()
    {
        var plan = Plan(Data(), device: Stick(64 * Gib));

        Assert.Equal(FileSystemKind.ExFat, plan.Partitions.Single().FileSystem);
    }

    [Fact]
    public void DataImage_ExplicitFat32_WithAHugeFile_IsRefused()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            Plan(Data(big: true), new TargetOptions { FileSystem = FileSystemKind.Fat32 }, Stick(16 * Gib)));

        Assert.Equal(ErrorCode.FileTooLargeForFileSystem, ex.Code);
    }

    [Theory]
    [InlineData(2, FileSystemKind.Fat12, 0x01)]
    [InlineData(16, FileSystemKind.Fat16, 0x04)]
    [InlineData(100, FileSystemKind.Fat16, 0x0E)]
    [InlineData(512, FileSystemKind.Fat16, 0x0E)]
    [InlineData(1024, FileSystemKind.Fat32, 0x0C)]
    public void FormatOnly_PicksTheFatTypeThatFitsTheSize(long megabytes, FileSystemKind expected, int mbrType)
    {
        var plan = Plan(Unknown(), device: Stick(megabytes * Mib));

        Assert.Equal(WriteMethod.FormatOnly, plan.WriteMethod);
        var main = plan.Partitions.Single();
        Assert.Equal(expected, main.FileSystem);
        Assert.Equal(mbrType, main.MbrType);
    }

    [Fact]
    public void FormatOnly_ExplicitFat16_OnABigStick_IsCappedAtWhatFat16Holds()
    {
        var plan = Plan(Unknown(), new TargetOptions { FileSystem = FileSystemKind.Fat16 }, Stick(8 * Gib));

        var main = plan.Partitions.Single();
        Assert.Equal(4000 * Mib, main.LengthBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.CapacityNotUsed);
    }

    [Fact]
    public void FormatOnly_Fat32OnAHugeStick_StopsAtTwoTebibytes()
    {
        var plan = Plan(Unknown(), new TargetOptions { FileSystem = FileSystemKind.Fat32, Scheme = PartitionScheme.Gpt }, Stick(3 * Tib));

        var main = plan.Partitions.Single();
        Assert.Equal(2 * Tib - Mib, main.LengthBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.CapacityNotUsed);
    }

    [Fact]
    public void FormatOnly_Fat32OnATinyStick_IsRefused()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            Plan(Unknown(), new TargetOptions { FileSystem = FileSystemKind.Fat32 }, Stick(20 * Mib)));

        Assert.Equal(ErrorCode.FileSystemTooSmall, ex.Code);
    }

    [Theory]
    [InlineData(FileSystemKind.Udf, 0x07)]
    [InlineData(FileSystemKind.ReFs, 0x07)]
    [InlineData(FileSystemKind.Ext3, 0x83)]
    [InlineData(FileSystemKind.Ntfs, 0x07)]
    public void FormatOnly_OtherFileSystems_GetTheirPartitionTypes(FileSystemKind fileSystem, int mbrType)
    {
        var plan = Plan(Unknown(), new TargetOptions { FileSystem = fileSystem });

        var main = plan.Partitions.Single();
        Assert.Equal(mbrType, main.MbrType);
        Assert.Equal(fileSystem == FileSystemKind.Ext3 ? GptTypes.LinuxData : GptTypes.BasicData, main.GptType);
    }

    [Fact]
    public void ClusterSize_IsPassedThroughAndValidated()
    {
        var plan = Plan(Unknown(), new TargetOptions { ClusterSizeBytes = 16384, FileSystem = FileSystemKind.Fat32 });
        Assert.Equal(16384, plan.Partitions.Single().ClusterSizeBytes);

        var ex = Assert.Throws<BootrixException>(() => Plan(Unknown(), new TargetOptions { ClusterSizeBytes = 3000 }));
        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void Dos_OnASmallStick_IsAnActiveFat16Partition_WithTheCompatibleType()
    {
        var plan = Plan(Dos(), device: Stick(1 * Gib));

        Assert.Equal(WriteMethod.FormatOnly, plan.WriteMethod);
        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal(BootMethod.FreeDos, plan.BootMethod);
        Assert.Equal(TargetFirmware.Bios, plan.Firmware);
        var main = Assert.Single(plan.Partitions);
        Assert.Equal(FileSystemKind.Fat16, main.FileSystem);
        Assert.Equal(MbrPartitionType.Fat16, main.MbrType);
        Assert.True(main.Active);
    }

    [Theory]
    [InlineData(4, 0x0B)]
    [InlineData(8, 0x0C)]
    [InlineData(64, 0x0C)]
    public void Dos_AboveTwoGibibytes_UsesFat32_AndTheChsTypeOnlyWithinTheFirstEightGigabytes(long gibibytes, int mbrType)
    {
        var plan = Plan(Dos(), device: Stick(gibibytes * Gib));

        var main = plan.Partitions.Single();
        Assert.Equal(FileSystemKind.Fat32, main.FileSystem);
        Assert.Equal(mbrType, main.MbrType);
    }

    [Fact]
    public void Dos_OnATinyStick_UsesTheSmallFat16Type()
    {
        var plan = Plan(Dos(), device: Stick(32 * Mib));

        Assert.Equal(MbrPartitionType.Fat16Small, plan.Partitions.Single().MbrType);
    }

    [Fact]
    public void Dos_ExplicitFat16_IsCappedAtTwoGibibytes()
    {
        var plan = Plan(Dos(), new TargetOptions { FileSystem = FileSystemKind.Fat16 }, Stick(8 * Gib));

        Assert.Equal(2 * Gib, plan.Partitions.Single().LengthBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.CapacityNotUsed);
    }

    [Fact]
    public void Dos_RefusesNtfsAndFourKibSectors()
    {
        var ntfs = Assert.Throws<BootrixException>(() => Plan(Dos(), new TargetOptions { FileSystem = FileSystemKind.Ntfs }));
        Assert.Equal(ErrorCode.FileSystemUnsupported, ntfs.Code);

        var fourK = Assert.Throws<BootrixException>(() => Plan(Dos(), device: Stick(8 * Gib, 4096)));
        Assert.Equal(ErrorCode.SectorSizeUnsupported, fourK.Code);
    }

    [Fact]
    public void Dos_ExplicitGpt_IsForcedToMbr()
    {
        var plan = Plan(Dos(), new TargetOptions { Scheme = PartitionScheme.Gpt });

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.DosNeedsMbr);
    }

    [Fact]
    public void Dos_OnAFloppy_IsASuperfloppyWithFat12()
    {
        var plan = Plan(Dos(), device: Floppy());

        Assert.True(plan.Superfloppy);
        Assert.Equal(PartitionScheme.Auto, plan.Scheme);
        Assert.Equal(BootMethod.FreeDos, plan.BootMethod);
        var main = Assert.Single(plan.Partitions);
        Assert.Equal(0, main.StartBytes);
        Assert.Equal(1_474_560, main.LengthBytes);
        Assert.Equal(FileSystemKind.Fat12, main.FileSystem);
        Assert.DoesNotContain(plan.Warnings, w => w.Code is PlanWarningCodes.SuperfloppyBoot or PlanWarningCodes.NonStandardFloppySize);
    }

    [Fact]
    public void Floppy_WithAnOddSize_WarnsAboutIt()
    {
        var plan = Plan(Dos(), device: Floppy(1_000_000));

        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.NonStandardFloppySize);
        Assert.Equal(FileSystemKind.Fat12, plan.Partitions.Single().FileSystem);
    }

    [Fact]
    public void Floppy_RefusesOtherFileSystemsAndBigImages()
    {
        var fs = Assert.Throws<BootrixException>(() =>
            Plan(Dos(), new TargetOptions { FileSystem = FileSystemKind.Fat32 }, Floppy()));
        Assert.Equal(ErrorCode.FileSystemUnsupported, fs.Code);

        var big = Assert.Throws<BootrixException>(() => Plan(WindowsIso(), device: Floppy()));
        Assert.Equal(ErrorCode.DeviceTooSmall, big.Code);
    }

    [Fact]
    public void Floppy_RawImage_IsCopiedAsASuperfloppy()
    {
        var plan = Plan(RawDisk(1_474_560), device: Floppy());

        Assert.Equal(WriteMethod.RawCopy, plan.WriteMethod);
        Assert.True(plan.Superfloppy);
        Assert.Empty(plan.Partitions);
        Assert.DoesNotContain(plan.Warnings, w => w.Code == PlanWarningCodes.RawCopyReadOnlyMedia);
    }

    [Fact]
    public void Superfloppy_OnAStick_PutsTheFileSystemAtLbaZero()
    {
        var plan = Plan(Data(), new TargetOptions { Superfloppy = true }, Stick(1 * Gib));

        Assert.True(plan.Superfloppy);
        Assert.Equal(PartitionScheme.Auto, plan.Scheme);
        var main = Assert.Single(plan.Partitions);
        Assert.Equal(0, main.StartBytes);
        Assert.Equal(1 * Gib, main.LengthBytes);
        Assert.Equal(FileSystemKind.Fat32, main.FileSystem);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.SuperfloppyBoot);
        Assert.Throws<InvalidOperationException>(() => plan.ToDiskLayout());
    }

    [Fact]
    public void Superfloppy_RefusesGptNtfsAndPersistenceIsDropped()
    {
        var gpt = Assert.Throws<BootrixException>(() =>
            Plan(Data(), new TargetOptions { Superfloppy = true, Scheme = PartitionScheme.Gpt }));
        Assert.Equal(ErrorCode.InvalidSpec, gpt.Code);

        var ntfs = Assert.Throws<BootrixException>(() =>
            Plan(Data(), new TargetOptions { Superfloppy = true, FileSystem = FileSystemKind.Ntfs }));
        Assert.Equal(ErrorCode.FileSystemUnsupported, ntfs.Code);

        var persistent = Plan(LinuxIso(), new TargetOptions { Superfloppy = true, PersistenceMegabytes = 512 });
        Assert.Contains(persistent.Warnings, w => w.Code == PlanWarningCodes.PersistenceUnsupported);
        Assert.Single(persistent.Partitions);
    }

    [Fact]
    public void Superfloppy_WindowsImageOnFat32_SplitsAWimThatIsTooBig()
    {
        var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { Superfloppy = true }, Stick(16 * Gib));

        Assert.True(plan.SplitWim);
        Assert.Equal(BootMethod.WindowsBootmgrBios | BootMethod.UefiNative, plan.BootMethod);
    }
}
