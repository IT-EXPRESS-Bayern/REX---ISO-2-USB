// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

public class LayoutPlannerDeviceTests
{
    private static MediaPlan Plan(ImageProfile image, TargetOptions? target = null, DeviceCaps? device = null) =>
        LayoutPlanner.Plan(image, target ?? new TargetOptions(), device ?? Stick(16 * Gib));

    [Fact]
    public void Drives_AboveTwoTebibytes_GetGptWithAWarning()
    {
        var plan = Plan(WindowsIso(), device: Stick(3 * Tib));

        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.LargeDriveGpt);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.BiosOnGpt);
        var main = plan.Partitions.Single();
        Assert.Equal(2 * Tib - Mib, main.LengthBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.CapacityNotUsed);
    }

    [Fact]
    public void Drives_AboveTwoTebibytes_WithExplicitMbr_StayMbrAndAreCapped()
    {
        var plan = Plan(Data(), new TargetOptions { Scheme = PartitionScheme.Mbr, FileSystem = FileSystemKind.Ntfs }, Stick(3 * Tib));

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.MbrCapped);
        Assert.True(plan.Partitions.Single().EndBytes <= uint.MaxValue * 512L);
    }

    [Fact]
    public void Drives_JustBelowTwoTebibytes_StayMbr()
    {
        var plan = Plan(Data(), new TargetOptions { FileSystem = FileSystemKind.Ntfs }, Stick(2 * Tib - Gib));

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.DoesNotContain(plan.Warnings, w => w.Code == PlanWarningCodes.LargeDriveGpt);
    }

    [Fact]
    public void FourKibSectors_OnUsb_WarnAboutTheBridge_AndStartAtOneMebibyte()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Uefi }, Stick(16 * Gib, 4096));

        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Equal(4096, plan.SectorSize);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.FourKnUsbBridge);
        Assert.DoesNotContain(plan.Warnings, w => w.Code == PlanWarningCodes.FourKnBios);
        var main = plan.Partitions.Single();
        Assert.Equal(Mib, main.StartBytes);
        Assert.Equal(256, main.StartLba(4096));
    }

    [Fact]
    public void FourKibSectors_WithBios_Warn()
    {
        var plan = Plan(WindowsIso(), device: Stick(16 * Gib, 4096));

        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.FourKnBios);
    }

    [Fact]
    public void FourKibSectors_OnAnInternalDrive_DoNotMentionTheBridge()
    {
        var device = Stick(16 * Gib, 4096) with { Bus = DeviceBus.Nvme, Removable = false, Medium = DeviceMedium.Hdd };

        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Uefi }, device);

        Assert.DoesNotContain(plan.Warnings, w => w.Code == PlanWarningCodes.FourKnUsbBridge);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.FixedDisk);
    }

    [Fact]
    public void FourKibSectors_WithAHybridImage_WarnThatTheTableAssumes512()
    {
        var plan = Plan(LinuxHybrid(), device: Stick(16 * Gib, 4096));

        Assert.Equal(WriteMethod.RawCopy, plan.WriteMethod);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.FourKnHybridImage);
    }

    [Fact]
    public void FourKibSectors_Fat32PartitionsStayFeasible()
    {
        var plan = Plan(Unknown(), new TargetOptions { FileSystem = FileSystemKind.Fat32 }, Stick(2 * Gib, 4096));

        Assert.Equal(FileSystemKind.Fat32, plan.Partitions.Single().FileSystem);
        Assert.Throws<BootrixException>(() =>
            Plan(Unknown(), new TargetOptions { FileSystem = FileSystemKind.Fat32 }, Stick(100 * Mib, 4096)));
    }

    [Fact]
    public void DeviceTooSmall_ReportsRequiredAndAvailableSize()
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(WindowsIso(), device: Stick(2 * Gib)));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
        Assert.Equal(2, ex.Arguments.Count);
        Assert.Equal("2 GiB", ex.Arguments[1]);
        Assert.StartsWith("3.", (string)ex.Arguments[0]!, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceTooSmall_CountsTheUefiHelperPartition()
    {
        var image = WindowsIso(bigWim: true);
        var tight = Stick(image.TotalBytes + 124 * Mib);

        var ex = Assert.Throws<BootrixException>(() => Plan(image, new TargetOptions { FileSystem = FileSystemKind.Ntfs }, tight));
        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);

        // Without the helper partition the same device is just big enough.
        Assert.NotNull(Plan(image, new TargetOptions { FileSystem = FileSystemKind.Fat32 }, tight));
    }

    [Fact]
    public void WindowsSetup_FitsAMarginallyLargeEnoughStick()
    {
        var image = WindowsIso();
        var plan = Plan(image, device: Stick(image.TotalBytes + 150 * Mib));

        Assert.True(plan.Partitions.Single().LengthBytes >= image.TotalBytes);
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(520)]
    [InlineData(0)]
    public void UnsupportedSectorSizes_AreRefused(int sectorSize)
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(Data(), device: Stick(1 * Gib, sectorSize)));

        Assert.Equal(ErrorCode.SectorSizeUnsupported, ex.Code);
    }

    [Fact]
    public void EmptyDevice_IsTooSmall()
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(Data(), device: Stick(0)));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
    }

    [Fact]
    public void FixedDisks_AreFlagged()
    {
        var plan = Plan(Data(), device: Stick(1 * Gib) with { Removable = false, Medium = DeviceMedium.Hdd, Bus = DeviceBus.Sata });

        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.FixedDisk);
    }

    [Fact]
    public void ToDiskLayout_CarriesPartitionsGeometrySignatureAndGuid()
    {
        var guid = new Guid("12345678-1234-1234-1234-123456789ABC");
        var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { Firmware = TargetFirmware.Uefi, FileSystem = FileSystemKind.Ntfs }, Stick(16 * Gib));

        var layout = plan.ToDiskLayout(mbrSignature: 0xABCD, diskGuid: guid, bootstrap: [1, 2]);

        Assert.Equal(PartitionScheme.Gpt, layout.Scheme);
        Assert.Equal(16 * Gib / 512, layout.TotalSectors);
        Assert.Equal(guid, layout.DiskGuid);
        Assert.Equal(0xABCDu, layout.MbrSignature);
        Assert.Equal([1, 2], layout.Bootstrap);
        Assert.Equal(2, layout.Partitions.Count);
        Assert.Equal(2048, layout.Partitions[0].StartLba);
        Assert.Equal(plan.Partitions[0].LengthBytes / 512, layout.Partitions[0].SectorCount);
        Assert.Equal(GptAttributes.NoDriveLetter, layout.Partitions[1].Attributes);
        Assert.Equal("UEFI_NTFS", layout.Partitions[1].Name);
    }

    [Fact]
    public void Plans_AreDeterministic()
    {
        var image = LinuxIso("ubuntu");
        var target = new TargetOptions { PersistenceMegabytes = 1024 };
        var device = Stick(16 * Gib);

        var first = LayoutPlanner.Plan(image, target, device);
        var second = LayoutPlanner.Plan(image, target, device);

        Assert.Equal(Describe(first), Describe(second));
        Assert.Equal(first.Partitions.Select(p => (p.StartBytes, p.LengthBytes)), second.Partitions.Select(p => (p.StartBytes, p.LengthBytes)));
        Assert.Equal(first.Warnings.Select(w => w.Code), second.Warnings.Select(w => w.Code));
    }

    [Fact]
    public void Plan_ValidatesItsArguments()
    {
        Assert.Throws<ArgumentNullException>(() => LayoutPlanner.Plan(null!, new TargetOptions(), Stick(1 * Gib)));
        Assert.Throws<ArgumentNullException>(() => LayoutPlanner.Plan(Data(), null!, Stick(1 * Gib)));
        Assert.Throws<ArgumentNullException>(() => LayoutPlanner.Plan(Data(), new TargetOptions(), null!));
    }
}
