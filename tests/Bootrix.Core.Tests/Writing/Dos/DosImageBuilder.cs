// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.IO;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Planning;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Dos;

namespace Bootrix.Core.Tests.Writing.Dos;

/// <summary>
/// Builds a DOS disk image the way the Windows writer builds a stick, from the same Core pieces and in the same
/// order: partition table from the plan, FAT formatted with the boot code of the DOS, files copied, MBR code installed.
/// Only where the bytes land (an image file instead of a physical disk) differs.
/// </summary>
internal static class DosImageBuilder
{
    public const long Mib = PlannerFixtures.Mib;

    public static MediaPlan PlanStick(long bytes, FileSystemKind fileSystem = FileSystemKind.Auto, bool legacy = false) =>
        LayoutPlanner.Plan(
            PlannerFixtures.Dos(),
            new TargetOptions { FileSystem = fileSystem, LegacyBiosFixes = legacy },
            PlannerFixtures.Stick(bytes));

    public static MediaPlan PlanFloppy(long bytes = 1_474_560) =>
        LayoutPlanner.Plan(PlannerFixtures.Dos(), new TargetOptions(), PlannerFixtures.Floppy(bytes));

    public static MediaPlan PlanSuperfloppyStick(long bytes, FileSystemKind fileSystem = FileSystemKind.Auto) =>
        LayoutPlanner.Plan(
            PlannerFixtures.Dos(),
            new TargetOptions { Superfloppy = true, FileSystem = fileSystem },
            PlannerFixtures.Stick(bytes));

    /// <summary>A stick image whose first partition carries <paramref name="system"/>; the file is created in a scratch location.</summary>
    public static TempImage BuildStick(MediaPlan plan, DosSystem system, bool forceBootDrive = false)
    {
        var image = new TempImage(plan.DeviceBytes);
        using var disk = image.Open();

        var layout = plan.ToDiskLayout(mbrSignature: 0x42445258);
        DiskLayoutWriter.WriteToStream(disk, layout, plan.SectorSize);

        var main = plan.Partitions.Single(p => p.Role == PartitionRole.Main);
        using (var volume = new StreamSlice(disk, main.StartBytes, main.LengthBytes))
        {
            FatFormatter.Format(volume, system.Customize(plan.ToFatOptions(main), plan.TotalSectors));
            DosVolumeWriter.Write(volume, system.Files);
        }

        var sector = new byte[plan.SectorSize];
        disk.Position = 0;
        disk.ReadExactly(sector);
        disk.Position = 0;
        disk.Write(DosMbr.Install(sector, forceBootDrive));
        disk.Flush();
        return image;
    }
}
