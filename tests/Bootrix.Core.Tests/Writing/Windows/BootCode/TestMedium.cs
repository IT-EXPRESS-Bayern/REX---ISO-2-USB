// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Images;
using Bootrix.Core.IO;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Writing.Windows.BootCode;

/// <summary>Plans a small Windows setup stick and lays it out in an image file the way the writer lays out a real disk.</summary>
internal static class TestMedium
{
    public const long Mib = 1024 * 1024;
    public const uint DiskSignature = 0x5EED1234;

    public static ImageProfile SmallWindowsImage(WindowsArch arch = WindowsArch.X64) => new()
    {
        Kind = ImageKind.WindowsSetup,
        VolumeLabel = "WINTEST",
        TotalBytes = 20 * Mib,
        LargestFileBytes = 10 * Mib,
        HasBiosBootFiles = true,
        HasEfiBootFiles = true,
        Arch = arch,
        WindowsBuild = 26200,
    };

    public static MediaPlan Plan(TargetOptions? target = null, long deviceBytes = 256 * Mib, int sectorSize = 512, ImageProfile? image = null)
    {
        var device = new DeviceCaps
        {
            SizeBytes = deviceBytes,
            LogicalSectorSize = sectorSize,
            PhysicalSectorSize = sectorSize,
            Bus = DeviceBus.Usb,
            Medium = DeviceMedium.Stick,
            Removable = true,
        };
        return LayoutPlanner.Plan(image ?? SmallWindowsImage(), target ?? new TargetOptions(), device);
    }

    /// <summary>Writes the partition table of the plan and formats its FAT partitions; <paramref name="customizeFat"/> is what a writer hands to the preparer.</summary>
    public static TempImage Realize(MediaPlan plan, Func<PlannedPartition, FatFormatOptions, FatFormatOptions>? customizeFat = null)
    {
        var image = new TempImage(plan.DeviceBytes);
        using var disk = image.Open();
        DiskLayoutWriter.WriteToStream(disk, plan.ToDiskLayout(DiskSignature, new Guid("A1B2C3D4-0000-4000-8000-0123456789AB")), plan.SectorSize);
        foreach (var partition in plan.Partitions.Where(p => p.FileSystem is FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32))
        {
            var options = plan.ToFatOptions(partition) with { AssumeZeroed = true };
            using var slice = new StreamSlice(disk, partition.StartBytes, partition.LengthBytes);
            FatFormatter.Format(slice, customizeFat?.Invoke(partition, options) ?? options);
        }

        return image;
    }

    /// <summary>Assembles <c>BootCode/TestVbr.asm</c> into 13 sectors of boot code (reserved sectors 0 to 12).</summary>
    public static byte[] AssembleTestVbr(TestDirectory dir)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Writing", "Windows", "BootCode", "TestVbr.asm");
        var output = dir.File("testvbr.bin");
        ExternalTools.Run("nasm", "-f", "bin", "-o", output, source);
        var code = File.ReadAllBytes(output);
        Array.Resize(ref code, 13 * 512);
        return code;
    }

    public static string Sfdisk(string imagePath) => ExternalTools.Run("sfdisk", "--dump", imagePath).Output;
}
