// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

public class LayoutPlannerLegacyTests
{
    private static MediaPlan Plan(ImageProfile image, TargetOptions target, DeviceCaps device) =>
        LayoutPlanner.Plan(image, target, device);

    private static readonly TargetOptions Legacy = new() { LegacyBiosFixes = true };

    [Fact]
    public void Default_StartsTheFirstPartitionAtSixtyFourKibibytes()
    {
        var plan = Plan(WindowsIso(), Legacy, Stick(6 * Gib));

        Assert.True(plan.LegacyBios);
        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Equal(128 * 512, plan.Partitions[0].StartBytes);
        Assert.True(plan.Partitions[0].Active);
    }

    [Fact]
    public void ClassicOffset_StartsAtLba63()
    {
        var plan = Plan(WindowsIso(), Legacy with { LegacyStart = LegacyPartitionStart.Lba63 }, Stick(6 * Gib));

        Assert.Equal(63 * 512, plan.Partitions[0].StartBytes);
        Assert.Equal(63, plan.Partitions[0].StartLba(512));
    }

    [Fact]
    public void PartitionsWithinTheFirstEightGigabytes_GetTheChsPartitionTypes()
    {
        var fat32 = Plan(WindowsIso(), Legacy, Stick(6 * Gib));
        Assert.Equal(MbrPartitionType.Fat32Chs, fat32.Partitions[0].MbrType);

        var fat16 = Plan(LinuxIso(), Legacy with { FileSystem = FileSystemKind.Fat16 }, Stick(1536 * Mib));
        Assert.Equal(MbrPartitionType.Fat16, fat16.Partitions[0].MbrType);
    }

    [Fact]
    public void PartitionsBeyondTheChsRange_FallBackToTheLbaTypes()
    {
        var fat32 = Plan(WindowsIso(), Legacy, Stick(32 * Gib));
        Assert.Equal(MbrPartitionType.Fat32Lba, fat32.Partitions[0].MbrType);

        // FAT16 ends within 4 GB, so it never leaves the CHS range; without the legacy flag it takes the LBA type.
        var fat16 = Plan(LinuxIso(), new TargetOptions { FileSystem = FileSystemKind.Fat16 }, Stick(16 * Gib));
        Assert.Equal(MbrPartitionType.Fat16Lba, fat16.Partitions[0].MbrType);
    }

    [Fact]
    public void Ntfs_KeepsTheCommonType_AndTheBootFlag()
    {
        var plan = Plan(WindowsIso(bigWim: true), Legacy with { FileSystem = FileSystemKind.Ntfs }, Stick(16 * Gib));

        Assert.Equal(MbrPartitionType.Ntfs, plan.Partitions[0].MbrType);
        Assert.True(plan.Partitions[0].Active);
        Assert.Equal(MbrPartitionType.EfiSystem, plan.Partitions[1].MbrType);
    }

    [Fact]
    public void BigDevices_AreLimitedToTheTwentyEightBitLbaRange()
    {
        var fat32 = Plan(WindowsIso(), Legacy, Stick(512 * Gib));
        var main = fat32.Partitions[0];
        Assert.True(main.EndBytes <= 128 * Gib);
        Assert.True(main.EndBytes > 128 * Gib - Mib);
        Assert.Contains(fat32.Warnings, w => w.Code == PlanWarningCodes.LegacyCapacityLimited);
    }

    [Fact]
    public void NonBootableMedia_IgnoreTheFixesWithAWarning()
    {
        var plan = Plan(Unknown(), Legacy, Stick(1 * Gib));

        Assert.False(plan.LegacyBios);
        Assert.Equal(Mib, plan.Partitions[0].StartBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.LegacyNeedsBios);
    }

    [Fact]
    public void FreeDos_WithLegacyFixes_KeepsFat16WithinTwoGibibytes_AtTheClassicOffset()
    {
        var plan = Plan(Dos(), Legacy with { LegacyStart = LegacyPartitionStart.Lba63, FileSystem = FileSystemKind.Fat16 }, Stick(16 * Gib));

        var main = plan.Partitions.Single();
        Assert.Equal(63 * 512, main.StartBytes);
        Assert.Equal(MbrPartitionType.Fat16, main.MbrType);
        Assert.True(main.EndBytes <= 63 * 512 + 2 * Gib);
        Assert.True(main.Active);
    }

    [Fact]
    public void ExplicitGpt_IsOverriddenByLegacyFixes()
    {
        var plan = Plan(WindowsIso(), Legacy with { Scheme = PartitionScheme.Gpt }, Stick(6 * Gib));

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.LegacyNeedsMbr);
    }

    [Fact]
    public void DevicesAboveTwoTebibytes_CanStillBeBootedByLegacyBios()
    {
        var plan = Plan(WindowsIso(), Legacy, Stick(4 * Tib));

        Assert.Equal(PartitionScheme.Mbr, plan.Scheme);
        Assert.True(plan.Partitions[0].EndBytes <= 128 * Gib);
    }

    [Fact]
    public void UefiOnlyMedia_IgnoreTheFixesWithAWarning()
    {
        var plan = Plan(WindowsIso(), Legacy with { Firmware = TargetFirmware.Uefi }, Stick(16 * Gib));

        Assert.False(plan.LegacyBios);
        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Equal(Mib, plan.Partitions[0].StartBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.LegacyNeedsBios);
    }

    [Fact]
    public void FourKibSectors_IgnoreTheFixesWithAWarning()
    {
        var plan = Plan(WindowsIso(), Legacy, Stick(16 * Gib, 4096));

        Assert.False(plan.LegacyBios);
        Assert.Equal(Mib, plan.Partitions[0].StartBytes);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.FourKnLegacyIgnored);
        Assert.Contains(plan.Warnings, w => w.Code == PlanWarningCodes.FourKnBios);
    }

    [Fact]
    public void Geometry_IsTheTranslated255By63()
    {
        var plan = Plan(WindowsIso(), Legacy, Stick(6 * Gib));

        Assert.Equal(ChsGeometry.Translated, plan.Geometry);
    }

    [Fact]
    public void Mbr_OfALegacyPlan_HasChsValuesThatAnOldBiosCanFollow()
    {
        var plan = Plan(WindowsIso(), Legacy with { LegacyStart = LegacyPartitionStart.Lba63 }, Stick(6 * Gib));

        var layout = plan.ToDiskLayout(mbrSignature: 0x11223344);
        var disk = new MemoryStream(new byte[6L * 1024 * 1024]);
        var shortLayout = layout with { TotalSectors = 6L * 1024 * 1024 / 512, Partitions = [layout.Partitions[0] with { SectorCount = 4000 }] };
        DiskLayoutWriter.WriteToStream(disk, shortLayout, 512);

        var mbr = Mbr.Parse(disk.ToArray().AsSpan(0, 512));
        Assert.Equal(new ChsAddress(0, 1, 1), mbr.Entries[0].FirstChs);
        Assert.True(mbr.Entries[0].IsActive);
        Assert.Equal(0x11223344u, mbr.DiskSignature);
    }
}
