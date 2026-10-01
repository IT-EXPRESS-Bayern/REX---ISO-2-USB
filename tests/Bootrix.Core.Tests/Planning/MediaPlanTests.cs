// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

public class MediaPlanTests
{
    [Fact]
    public void ToFatOptions_CarriesPositionGeometryLabelAndClusterSize()
    {
        var plan = LayoutPlanner.Plan(
            WindowsIso(), new TargetOptions { ClusterSizeBytes = 16384, Label = "Setup" }, Stick(16 * Gib));
        var main = plan.Partitions.Single();

        var options = plan.ToFatOptions(main);

        Assert.Equal(main.LengthBytes, options.TotalBytes);
        Assert.Equal(512, options.BytesPerSector);
        Assert.Equal(FatType.Fat32, options.Type);
        Assert.Equal(32, options.SectorsPerCluster);
        Assert.Equal("Setup", options.Label);
        Assert.Equal(2048u, options.HiddenSectors);
        Assert.Equal(63, options.SectorsPerTrack);
        Assert.Equal(255, options.Heads);
        Assert.Equal(0x80, options.DriveNumber);
        Assert.Equal(0xF8, options.MediaDescriptor);
    }

    [Fact]
    public void ToFatOptions_ForALegacyPlan_UsesTheClassicHiddenSectors()
    {
        var plan = LayoutPlanner.Plan(
            WindowsIso(), new TargetOptions { LegacyBiosFixes = true, LegacyStart = LegacyPartitionStart.Lba63 }, Stick(6 * Gib));

        Assert.Equal(63u, plan.ToFatOptions(plan.Partitions[0]).HiddenSectors);
    }

    [Fact]
    public void ToFatOptions_ForAFourKibStick_UsesItsSectorSize()
    {
        var plan = LayoutPlanner.Plan(WindowsIso(), new TargetOptions(), Stick(16 * Gib, 4096));

        var options = plan.ToFatOptions(plan.Partitions[0]);

        Assert.Equal(4096, options.BytesPerSector);
        Assert.Equal(256u, options.HiddenSectors);
    }

    [Fact]
    public void ToFatOptions_ForAFloppy_ReproducesTheStandardFormat()
    {
        var plan = LayoutPlanner.Plan(Dos(), new TargetOptions { Label = "BOOT" }, Floppy());

        var options = plan.ToFatOptions(plan.Partitions[0]);

        Assert.Equal(FloppyPreset.All.Single(p => p.Name == "1.44M").ToOptions() with { Label = "BOOT" }, options);
        Assert.Equal(0, options.DriveNumber);
    }

    [Fact]
    public void ToFatOptions_ForASuperfloppyStick_HasNoHiddenSectorsAndDriveZero()
    {
        var plan = LayoutPlanner.Plan(Data(), new TargetOptions { Superfloppy = true }, Stick(1 * Gib));

        var options = plan.ToFatOptions(plan.Partitions[0]);

        Assert.Equal(0u, options.HiddenSectors);
        Assert.Equal(0, options.DriveNumber);
        Assert.Equal(1 * Gib, options.TotalBytes);
    }

    [Fact]
    public void ToFatOptions_RefusesPartitionsThatAreNotFat()
    {
        var plan = LayoutPlanner.Plan(WindowsIso(bigWim: true), new TargetOptions { FileSystem = FileSystemKind.Ntfs }, Stick(16 * Gib));

        Assert.Throws<InvalidOperationException>(() => plan.ToFatOptions(plan.Partitions[0]));
        Assert.Throws<InvalidOperationException>(() => plan.ToFatOptions(plan.Partitions[1]));
        Assert.Throws<ArgumentNullException>(() => plan.ToFatOptions(null!));
    }

    [Fact]
    public void PlannedPartition_ConvertsBytesToSectors()
    {
        var partition = new PlannedPartition { Role = PartitionRole.Main, StartBytes = Mib, LengthBytes = 100 * Mib };

        Assert.Equal(2048, partition.StartLba(512));
        Assert.Equal(256, partition.StartLba(4096));
        Assert.Equal(204_800, partition.SectorCount(512));
        Assert.Equal(partition.StartBytes + partition.LengthBytes, partition.EndBytes);

        var entry = partition.ToEntry(512);
        Assert.Equal(2048, entry.StartLba);
        Assert.Equal(204_800, entry.SectorCount);
        Assert.Equal(2048 + 204_800 - 1, entry.EndLba);
    }

    [Fact]
    public void TotalSectors_FollowsTheDeviceAndSectorSize()
    {
        var plan = LayoutPlanner.Plan(Data(), new TargetOptions(), Stick(8 * Gib, 4096));

        Assert.Equal(8 * Gib / 4096, plan.TotalSectors);
    }

    [Fact]
    public void ToDiskLayout_OfAFilePlan_WritesAValidTable()
    {
        var plan = LayoutPlanner.Plan(WindowsIso(bigWim: true), new TargetOptions { Firmware = TargetFirmware.Uefi, FileSystem = FileSystemKind.Ntfs }, Stick(16 * Gib));
        var disk = new Tooling.SparseMemoryStream(plan.DeviceBytes);

        DiskLayoutWriter.WriteToStream(disk, plan.ToDiskLayout(), 512);

        var gpt = Gpt.Read(disk, 512);
        Assert.NotNull(gpt);
        Assert.Equal(2, gpt.Partitions.Count);
        Assert.Equal(GptAttributes.NoDriveLetter, gpt.Partitions[1].Attributes);
        Assert.Equal("UEFI_NTFS", gpt.Partitions[1].Name);
    }
}
