// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Images;
using Bootrix.Core.IO;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

/// <summary>Plans a medium, writes its partition table and FAT volumes into a sparse image and lets the standard Linux tools judge the whole.</summary>
public class LayoutPlannerEndToEndTests
{
    private static TempImage Realize(MediaPlan plan)
    {
        var image = new TempImage(plan.DeviceBytes);
        using var disk = image.Open();
        if (plan.Scheme != PartitionScheme.Auto)
        {
            DiskLayoutWriter.WriteToStream(disk, plan.ToDiskLayout(mbrSignature: 0x5EED1234, diskGuid: new Guid("A1B2C3D4-0000-4000-8000-0123456789AB")), plan.SectorSize);
        }

        foreach (var partition in plan.Partitions.Where(p => p.FileSystem is FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32))
        {
            using var slice = new StreamSlice(disk, partition.StartBytes, partition.LengthBytes);
            FatFormatter.Format(slice, plan.ToFatOptions(partition) with { AssumeZeroed = true });
        }

        return image;
    }

    private static MediaPlan Plan(ImageProfile image, TargetOptions target, DeviceCaps device) => LayoutPlanner.Plan(image, target, device);

    [RequiresToolFact("sgdisk", "sfdisk", "mdir", "minfo", "mcopy")]
    public void WindowsUefiOnGpt_IsValidAndItsFat32VolumeKnowsWhereItLives()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Uefi }, Stick(8 * Gib));
        using var image = Realize(plan);

        var verify = ExternalTools.Run("sgdisk", "--verify", image.Path);
        Assert.Contains("No problems found", verify.Output, StringComparison.Ordinal);

        var dump = ExternalTools.Run("sfdisk", "--dump", image.Path).Output;
        Assert.Matches(@"start=\s*2048, size=\s*\d+, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7, uuid=[0-9A-F-]+, name=""CCCOMA_X64FRE_DE-DE_DV9""", dump);

        var start = plan.Partitions[0].StartBytes;
        var fields = FatVerifier.Minfo($"{image.Path}@@{start}");
        Assert.Equal("2048", fields["hidden sectors"]);
        Assert.Equal("FAT32", fields["disk type"]);
        Assert.Equal("CCCOMA_X64F", fields["disk label"]);
        FatVerifier.MtoolsRoundTrip(image.Path, start);
    }

    [RequiresToolFact("sfdisk", "mdir", "minfo", "fdisk")]
    public void WindowsBiosAndUefiOnMbr_IsBootableAndUsesTheTranslatedGeometry()
    {
        var plan = Plan(WindowsIso(), new TargetOptions(), Stick(64 * Gib));
        using var image = Realize(plan);

        var dump = ExternalTools.Run("sfdisk", "--dump", image.Path).Output;
        Assert.Contains("label-id: 0x5eed1234", dump, StringComparison.Ordinal);
        Assert.Matches(@"start=\s*2048, size=\s*\d+, type=c, bootable", dump);

        var fields = FatVerifier.Minfo($"{image.Path}@@{plan.Partitions[0].StartBytes}");
        Assert.Equal("255", fields["heads"]);
        Assert.Equal("63", fields["sectors per track"]);
        Assert.Equal("0x80", fields["physical drive id"]);
        Assert.Equal("2048", fields["hidden sectors"]);
    }

    [RequiresToolFact("sgdisk", "sfdisk", "mdir")]
    public void WindowsUefiNtfs_OnGpt_HasTheHiddenHelperPartitionAtTheEnd()
    {
        var plan = Plan(WindowsIso(bigWim: true), new TargetOptions { Firmware = TargetFirmware.Uefi, FileSystem = FileSystemKind.Ntfs }, Stick(16 * Gib));
        using var image = Realize(plan);

        Assert.Contains("No problems found", ExternalTools.Run("sgdisk", "--verify", image.Path).Output, StringComparison.Ordinal);
        var helper = ExternalTools.Run("sgdisk", "--info=2", image.Path).Output;
        Assert.Contains("Attribute flags: 8000000000000000", helper, StringComparison.Ordinal);
        Assert.Contains("Partition name: 'UEFI_NTFS'", helper, StringComparison.Ordinal);
        Assert.Contains("Partition size: 2048 sectors", helper, StringComparison.Ordinal);

        var first = ExternalTools.Run("sgdisk", "--info=1", image.Path).Output;
        Assert.Contains("(Microsoft basic data)", first, StringComparison.Ordinal);
    }

    [RequiresToolFact("sgdisk")]
    public void UbuntuOnGpt_GetsABiosBootPartitionForGrubInFrontOfTheData()
    {
        var plan = Plan(LinuxIso("ubuntu"), new TargetOptions { Scheme = PartitionScheme.Gpt }, Stick(8 * Gib));
        using var image = Realize(plan);

        Assert.Contains("No problems found", ExternalTools.Run("sgdisk", "--verify", image.Path).Output, StringComparison.Ordinal);
        var first = ExternalTools.Run("sgdisk", "--info=1", image.Path).Output;
        Assert.Contains("(BIOS boot partition)", first, StringComparison.Ordinal);
        Assert.Contains("First sector: 2048", first, StringComparison.Ordinal);
        Assert.Contains("Partition size: 2048 sectors", first, StringComparison.Ordinal);
        Assert.Contains("First sector: 4096", ExternalTools.Run("sgdisk", "--info=2", image.Path).Output, StringComparison.Ordinal);
    }

    [RequiresToolFact("fdisk", "minfo")]
    public void LegacyBios_ClassicOffset_ProducesChsValuesAndABpbThatAgree()
    {
        var target = new TargetOptions { LegacyBiosFixes = true, LegacyStart = LegacyPartitionStart.Lba63 };
        var plan = Plan(WindowsIso(), target, Stick(6 * Gib));
        using var image = Realize(plan);

        var listing = ExternalTools.RunWithInput("fdisk", "x\np\nq\n", image.Path).Output;
        Assert.Matches(@"\*\s+63\s+\d+\s+\d+\s+b W95 FAT32\s+0/1/1\s+", listing);

        var fields = FatVerifier.Minfo($"{image.Path}@@{63 * 512}");
        Assert.Equal("63", fields["hidden sectors"]);
        Assert.Equal("63", fields["sectors per track"]);
        Assert.Equal("255", fields["heads"]);
    }

    [RequiresToolFact("sfdisk", "mdir", "minfo")]
    public void FreeDos_OnAOneGibibyteStick_IsAnActiveFat16Partition()
    {
        var plan = Plan(Dos(), new TargetOptions(), Stick(1 * Gib));
        using var image = Realize(plan);

        var dump = ExternalTools.Run("sfdisk", "--dump", image.Path).Output;
        Assert.Matches(@"start=\s*2048, size=\s*\d+, type=6, bootable", dump);
        var fields = FatVerifier.Minfo($"{image.Path}@@{Mib}");
        Assert.Equal("FAT16", fields["disk type"]);
        Assert.Equal("2048", fields["hidden sectors"]);
    }

    [RequiresToolFact("sfdisk")]
    public void LinuxIsoWithPersistence_EndsInAnExt3Partition()
    {
        var plan = Plan(LinuxIso(total: 1 * Gib), new TargetOptions { PersistenceMegabytes = 2048 }, Stick(8 * Gib));
        using var image = Realize(plan);

        var dump = ExternalTools.Run("sfdisk", "--dump", image.Path).Output;
        Assert.Matches(@"start=\s*2048, size=\s*\d+, type=c, bootable", dump);
        Assert.Matches(@"size=\s*4194304, type=83", dump);
    }

    [RequiresToolFact("fdisk", "sgdisk")]
    public void FourKibSectors_WindowsUefi_IsReadByFdisk()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Uefi }, Stick(16 * Gib, 4096));
        using var image = Realize(plan);

        var listing = ExternalTools.Run("fdisk", "-b", "4096", "-l", image.Path);

        Assert.True(listing.ExitCode == 0, listing.Combined);
        Assert.Contains("Disklabel type: gpt", listing.Output, StringComparison.Ordinal);
        Assert.Matches(@"\.img1\s+256\s+\d+\s+\d+", listing.Output);
        Assert.DoesNotContain("mismatch", listing.Combined, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresToolFact("sgdisk")]
    public void WindowsToGo_Gpt_HasEspMsrAndBasicDataInOrder()
    {
        var plan = Plan(WindowsIso(), new TargetOptions { WindowsToGo = true, Firmware = TargetFirmware.Uefi }, Stick(64 * Gib));
        using var image = Realize(plan);

        Assert.Contains("No problems found", ExternalTools.Run("sgdisk", "--verify", image.Path).Output, StringComparison.Ordinal);
        Assert.Contains("(EFI system partition)", ExternalTools.Run("sgdisk", "--info=1", image.Path).Output, StringComparison.Ordinal);
        Assert.Contains("(Microsoft reserved)", ExternalTools.Run("sgdisk", "--info=2", image.Path).Output, StringComparison.Ordinal);
        Assert.Contains("(Microsoft basic data)", ExternalTools.Run("sgdisk", "--info=3", image.Path).Output, StringComparison.Ordinal);
    }

    [RequiresToolFact("sgdisk", "mdir")]
    public void ThreeTebibyteDevice_GetsGptAndA_TwoTebibyteFat32Volume()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var plan = Plan(WindowsIso(), new TargetOptions { Firmware = TargetFirmware.Uefi }, Stick(3 * Tib));
        using var image = Realize(plan);

        Assert.Contains("No problems found", ExternalTools.Run("sgdisk", "--verify", image.Path).Output, StringComparison.Ordinal);
        var result = ExternalTools.Run("mdir", "-i", $"{image.Path}@@{Mib}", "::");
        Assert.True(result.ExitCode == 0, result.Combined);
    }

    [RequiresToolFact("fsck.vfat", "mdir", "mcopy", "minfo")]
    public void SuperfloppyStick_IsOneFat32VolumeWithoutPartitionTable()
    {
        var plan = Plan(Data(), new TargetOptions { Superfloppy = true }, Stick(600 * Mib));
        using var image = Realize(plan);

        var report = FatVerifier.Fsck(image.Path);

        Assert.Equal(FileSystemKind.Fat32, plan.Partitions[0].FileSystem);
        Assert.True(report.DataClusters > 65524);
        Assert.Equal("0x0", FatVerifier.Minfo(image.Path)["physical drive id"]);
        Assert.Equal("0", FatVerifier.Minfo(image.Path)["hidden sectors"]);
        FatVerifier.MtoolsRoundTrip(image.Path);
    }

    [RequiresToolFact("fsck.vfat", "mdir", "minfo")]
    public void Floppy_BecomesAStandardDiskette()
    {
        var plan = Plan(Dos(), new TargetOptions { Label = "FREEDOS" }, Floppy());
        using var image = Realize(plan);

        FatVerifier.Fsck(image.Path);
        var fields = FatVerifier.Minfo(image.Path);
        Assert.Equal("FAT12", fields["disk type"]);
        Assert.Equal("0xf0", fields["media descriptor byte"]);
        Assert.Equal("18", fields["sectors per track"]);
        Assert.Equal("2", fields["heads"]);
        Assert.Equal("FREEDOS", fields["disk label"]);
    }

    [RequiresToolTheory("fsck.vfat")]
    [InlineData(100)]
    [InlineData(256)]
    public void SmallDataSticks_FormatCleanlyAtTheirPlannedSize(long megabytes)
    {
        var plan = Plan(Unknown(), new TargetOptions(), Stick(megabytes * Mib));
        using var image = Realize(plan);
        var partition = plan.Partitions.Single();

        using var volume = new TempImage(0);
        using (var source = image.Open())
        using (var target = new FileStream(volume.Path, FileMode.Create, FileAccess.Write))
        {
            source.Position = partition.StartBytes;
            var buffer = new byte[1 << 20];
            var remaining = partition.LengthBytes;
            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                target.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        FatVerifier.Fsck(volume.Path);
    }
}
