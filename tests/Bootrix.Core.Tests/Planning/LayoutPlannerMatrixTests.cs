// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Tooling;
using static Bootrix.Core.Tests.Planning.PlannerFixtures;

namespace Bootrix.Core.Tests.Planning;

/// <summary>The decision table as data: image kind, firmware, scheme, file system, legacy flag, size and sector size map to one plan summary.</summary>
public class LayoutPlannerMatrixTests
{
    private const TargetFirmware Auto = TargetFirmware.Auto;
    private const TargetFirmware Bios = TargetFirmware.Bios;
    private const TargetFirmware Uefi = TargetFirmware.Uefi;
    private const TargetFirmware Both = TargetFirmware.BiosAndUefi;
    private const PartitionScheme SchemeAuto = PartitionScheme.Auto;
    private const FileSystemKind FsAuto = FileSystemKind.Auto;

    public static TheoryData<string, TargetFirmware, PartitionScheme, FileSystemKind, bool, long, int, string> Decisions => new()
    {
        // Windows setup media
        { "win", Uefi, SchemeAuto, FsAuto, false, 8, 512, "Gpt/ExtractFiles/UefiNative|Main:Fat32:basic" },
        { "win", Bios, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/WindowsBootmgrBios|Main:Fat32:0x0C*" },
        { "win", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:0x0C*" },
        { "win", Both, SchemeAuto, FsAuto, false, 64, 512, "Mbr/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:0x0C*" },
        { "win", Auto, PartitionScheme.Gpt, FsAuto, false, 64, 512, "Gpt/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:basic" },
        { "win", Auto, SchemeAuto, FsAuto, false, 3072, 512, "Gpt/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:basic" },
        { "win", Uefi, SchemeAuto, FsAuto, false, 3072, 512, "Gpt/ExtractFiles/UefiNative|Main:Fat32:basic" },
        { "win", Auto, SchemeAuto, FsAuto, true, 8, 512, "Mbr/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:0x0C*" },
        { "win", Auto, SchemeAuto, FsAuto, true, 6, 512, "Mbr/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:0x0B*" },
        { "win", Auto, SchemeAuto, FsAuto, false, 8, 4096, "Mbr/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:0x0C*" },
        { "win", Uefi, SchemeAuto, FsAuto, false, 8, 4096, "Gpt/ExtractFiles/UefiNative|Main:Fat32:basic" },
        { "win-big", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:0x0C*" },
        { "win-big", Uefi, SchemeAuto, FsAuto, false, 8, 512, "Gpt/ExtractFiles/UefiNative|Main:Fat32:basic" },
        { "win-big", Uefi, SchemeAuto, FileSystemKind.Ntfs, false, 8, 512, "Gpt/ExtractFiles/UefiNtfs|Main:Ntfs:basic UefiNtfs:raw:basic+nodrive" },
        { "win-big", Auto, SchemeAuto, FileSystemKind.Ntfs, false, 8, 512, "Mbr/ExtractFiles/WindowsBootmgrBios, UefiNtfs|Main:Ntfs:0x07* UefiNtfs:raw:0xEF" },
        { "win-big", Bios, SchemeAuto, FileSystemKind.Ntfs, false, 64, 512, "Mbr/ExtractFiles/WindowsBootmgrBios|Main:Ntfs:0x07*" },
        { "win-big", Bios, PartitionScheme.Gpt, FileSystemKind.Ntfs, false, 64, 512, "Gpt/ExtractFiles/WindowsBootmgrBios|Main:Ntfs:basic" },
        { "win-big", Uefi, PartitionScheme.Mbr, FileSystemKind.ExFat, false, 64, 512, "Mbr/ExtractFiles/UefiNtfs|Main:ExFat:0x07 UefiNtfs:raw:0xEF" },
        { "win-big", Auto, SchemeAuto, FileSystemKind.Fat32, true, 64, 512, "Mbr/ExtractFiles/WindowsBootmgrBios, UefiNative|Main:Fat32:0x0C*" },
        { "win-arm", Auto, SchemeAuto, FsAuto, false, 8, 512, "Gpt/ExtractFiles/UefiNative|Main:Fat32:basic" },
        { "win-arm", Bios, SchemeAuto, FileSystemKind.Ntfs, false, 8, 512, "Gpt/ExtractFiles/UefiNtfs|Main:Ntfs:basic UefiNtfs:raw:basic+nodrive" },

        // Linux: hybrid images are copied raw, everything else is extracted
        { "linux-hybrid", Auto, SchemeAuto, FsAuto, false, 8, 512, "Auto/RawCopy/ImageNative" },
        { "linux-hybrid", Uefi, SchemeAuto, FileSystemKind.Ntfs, true, 8, 4096, "Auto/RawCopy/ImageNative" },
        { "raw", Auto, PartitionScheme.Gpt, FileSystemKind.Ntfs, true, 8, 512, "Auto/RawCopy/ImageNative" },
        { "linux-iso", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/UefiNative, SyslinuxMbr|Main:Fat32:0x0C*" },
        { "linux-iso", Uefi, PartitionScheme.Gpt, FsAuto, false, 8, 4096, "Gpt/ExtractFiles/UefiNative|Main:Fat32:basic" },
        { "linux-iso", Auto, PartitionScheme.Gpt, FsAuto, false, 64, 512, "Gpt/ExtractFiles/UefiNative, SyslinuxMbr|Main:Fat32:basic+bios" },
        { "ubuntu-iso", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/UefiNative, Grub|Main:Fat32:0x0C*" },
        { "ubuntu-iso", Auto, SchemeAuto, FsAuto, true, 8, 512, "Mbr/ExtractFiles/UefiNative, Grub|Main:Fat32:0x0C*" },
        { "ubuntu-iso", Auto, PartitionScheme.Gpt, FsAuto, false, 64, 512, "Gpt/ExtractFiles/UefiNative, Grub|BiosBoot:raw:biosboot Main:Fat32:basic" },
        { "ubuntu-iso", Bios, PartitionScheme.Gpt, FsAuto, false, 64, 512, "Gpt/ExtractFiles/Grub|BiosBoot:raw:biosboot Main:Fat32:basic" },
        { "ubuntu-iso", Auto, SchemeAuto, FileSystemKind.ExFat, false, 64, 512, "Mbr/ExtractFiles/UefiNtfs, Grub|Main:ExFat:0x07* UefiNtfs:raw:0xEF" },
        { "linux-iso-big", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/UefiNtfs, SyslinuxMbr|Main:Ntfs:0x07* UefiNtfs:raw:0xEF" },
        { "linux-iso-big", Uefi, SchemeAuto, FsAuto, false, 8, 512, "Gpt/ExtractFiles/UefiNtfs|Main:Ntfs:basic UefiNtfs:raw:basic+nodrive" },
        { "linux-iso-big", Bios, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/SyslinuxMbr|Main:Ntfs:0x07*" },

        // Data media and plain formatting
        { "data", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/None|Main:Fat32:0x0C" },
        { "data", Auto, SchemeAuto, FsAuto, false, 64, 512, "Mbr/ExtractFiles/None|Main:ExFat:0x07" },
        { "data", Auto, SchemeAuto, FileSystemKind.Fat32, false, 64, 512, "Mbr/ExtractFiles/None|Main:Fat32:0x0C" },
        { "data", Auto, SchemeAuto, FileSystemKind.Ntfs, false, 64, 512, "Mbr/ExtractFiles/None|Main:Ntfs:0x07" },
        { "data-big", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/ExtractFiles/None|Main:ExFat:0x07" },
        { "data-big", Auto, SchemeAuto, FsAuto, false, 3072, 512, "Gpt/ExtractFiles/None|Main:ExFat:basic" },
        { "unknown", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/FormatOnly/None|Main:Fat32:0x0C" },
        { "unknown", Uefi, SchemeAuto, FsAuto, false, 3072, 512, "Gpt/FormatOnly/None|Main:ExFat:basic" },
        { "unknown", Auto, PartitionScheme.Mbr, FileSystemKind.Ntfs, false, 3072, 512, "Mbr/FormatOnly/None|Main:Ntfs:0x07" },
        { "unknown", Auto, PartitionScheme.Gpt, FileSystemKind.Ext3, false, 8, 512, "Gpt/FormatOnly/None|Main:Ext3:linux" },

        // FreeDOS
        { "dos", Auto, SchemeAuto, FsAuto, false, 1, 512, "Mbr/FormatOnly/FreeDos|Main:Fat16:0x06*" },
        { "dos", Auto, SchemeAuto, FsAuto, false, 4, 512, "Mbr/FormatOnly/FreeDos|Main:Fat32:0x0B*" },
        { "dos", Auto, SchemeAuto, FsAuto, false, 8, 512, "Mbr/FormatOnly/FreeDos|Main:Fat32:0x0C*" },
        { "dos", Auto, PartitionScheme.Gpt, FileSystemKind.Fat32, true, 64, 512, "Mbr/FormatOnly/FreeDos|Main:Fat32:0x0C*" },
        { "dos", Uefi, SchemeAuto, FsAuto, false, 8, 512, "Mbr/FormatOnly/FreeDos|Main:Fat32:0x0C*" },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Plan_FollowsTheDecisionTable(
        string image, TargetFirmware firmware, PartitionScheme scheme, FileSystemKind fileSystem, bool legacy, long gibibytes, int sectorSize, string expected)
    {
        var target = new TargetOptions { Firmware = firmware, Scheme = scheme, FileSystem = fileSystem, LegacyBiosFixes = legacy };

        var plan = LayoutPlanner.Plan(ByKey(image), target, Stick(gibibytes * Gib, sectorSize));

        Assert.Equal(expected, Describe(plan));
    }

    public static TheoryData<string, FileSystemKind, int, ErrorCode> Refusals => new()
    {
        { "win", FileSystemKind.Udf, 512, ErrorCode.FileSystemUnsupported },
        { "win", FileSystemKind.Fat16, 512, ErrorCode.FileSystemUnsupported },
        { "linux-iso", FileSystemKind.ReFs, 512, ErrorCode.FileSystemUnsupported },
        { "linux-iso-big", FileSystemKind.Fat32, 512, ErrorCode.FileTooLargeForFileSystem },
        { "data-big", FileSystemKind.Fat32, 512, ErrorCode.FileTooLargeForFileSystem },
        { "dos", FileSystemKind.Ntfs, 512, ErrorCode.FileSystemUnsupported },
        { "dos", FileSystemKind.Auto, 4096, ErrorCode.SectorSizeUnsupported },
        { "win", FileSystemKind.Auto, 2048, ErrorCode.SectorSizeUnsupported },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Plan_RefusesWhatCannotWork(string image, FileSystemKind fileSystem, int sectorSize, ErrorCode expected)
    {
        var ex = Assert.Throws<BootrixException>(() =>
            LayoutPlanner.Plan(ByKey(image), new TargetOptions { FileSystem = fileSystem }, Stick(64 * Gib, sectorSize)));

        Assert.Equal(expected, ex.Code);
    }

    public static TheoryData<string, long> TooSmall => new()
    {
        { "win", 1 * Gib },
        { "win", 3 * Gib },
        { "win-big", 6 * Gib },
        { "linux-hybrid", 2 * Gib },
        { "linux-iso", 512 * Mib },
        { "raw", 1 * Gib },
        { "data-big", 5 * Gib },
    };

    [Theory]
    [MemberData(nameof(TooSmall))]
    public void Plan_ReportsDevicesThatAreTooSmall(string image, long deviceBytes)
    {
        var ex = Assert.Throws<BootrixException>(() => LayoutPlanner.Plan(ByKey(image), new TargetOptions(), Stick(deviceBytes)));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
        Assert.Equal(2, ex.Arguments.Count);
    }

    private static readonly string[] ImageKeys =
        ["win", "win-big", "win-arm", "linux-hybrid", "linux-iso", "ubuntu-iso", "linux-iso-big", "dos", "data", "data-big", "raw", "unknown"];

    private static readonly ErrorCode[] ExpectedErrors =
    [
        ErrorCode.FileSystemUnsupported, ErrorCode.FileTooLargeForFileSystem, ErrorCode.SectorSizeUnsupported, ErrorCode.DeviceTooSmall,
        ErrorCode.FileSystemTooSmall, ErrorCode.WriteModeUnsupported, ErrorCode.PersistenceTooSmall, ErrorCode.InvalidSpec,
    ];

    /// <summary>Every combination either yields a plan that obeys the format rules or one of the documented errors.</summary>
    [Fact]
    public void EveryCombination_YieldsAConsistentPlanOrADocumentedError()
    {
        var failures = new List<string>();
        var plans = 0;

        foreach (var image in ImageKeys)
        foreach (var firmware in new[] { Auto, Bios, Uefi, Both })
        foreach (var scheme in new[] { SchemeAuto, PartitionScheme.Mbr, PartitionScheme.Gpt })
        foreach (var fileSystem in new[] { FsAuto, FileSystemKind.Fat32, FileSystemKind.Ntfs, FileSystemKind.ExFat, FileSystemKind.Fat16 })
        foreach (var legacy in new[] { false, true })
        foreach (var size in new[] { 64 * Mib, 2 * Gib, 8 * Gib, 64 * Gib, 3 * Tib })
        foreach (var sectorSize in new[] { 512, 4096 })
        foreach (var persistence in new[] { 0, 2048 })
        foreach (var superfloppy in new[] { false, true })
        {
            var target = new TargetOptions
            {
                Firmware = firmware,
                Scheme = scheme,
                FileSystem = fileSystem,
                LegacyBiosFixes = legacy,
                PersistenceMegabytes = persistence,
                Superfloppy = superfloppy,
            };
            var label = $"{image} {firmware} {scheme} {fileSystem} legacy={legacy} {size / Mib} MiB ss={sectorSize} persist={persistence} sfd={superfloppy}";

            MediaPlan plan;
            try
            {
                plan = LayoutPlanner.Plan(ByKey(image), target, Stick(size, sectorSize));
            }
            catch (BootrixException ex)
            {
                if (!ExpectedErrors.Contains(ex.Code))
                {
                    failures.Add($"{label}: unexpected error {ex.Code}");
                }

                continue;
            }

            plans++;
            foreach (var problem in Check(plan, ByKey(image), size))
            {
                failures.Add($"{label}: {problem}");
            }

            if (failures.Count > 25)
            {
                break;
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(25)));
        Assert.True(plans > 20_000, $"only {plans} combinations produced a plan");
    }

    private static IEnumerable<string> Check(MediaPlan plan, ImageProfile image, long deviceBytes)
    {
        var ss = plan.SectorSize;
        var parts = plan.Partitions;

        if (plan.DeviceBytes > deviceBytes || plan.DeviceBytes % ss != 0)
        {
            yield return "device size is inconsistent";
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (p.StartBytes < 0 || p.LengthBytes <= 0 || p.EndBytes > plan.DeviceBytes)
            {
                yield return $"partition {i} lies outside the device";
            }

            if (p.StartBytes % ss != 0 || p.LengthBytes % ss != 0)
            {
                yield return $"partition {i} is not sector aligned";
            }

            if (i > 0 && p.StartBytes < parts[i - 1].EndBytes)
            {
                yield return $"partition {i} overlaps or precedes its predecessor";
            }

            if (!plan.LegacyBios && !plan.Superfloppy && p.StartBytes % Mib != 0)
            {
                yield return $"partition {i} is not 1 MiB aligned";
            }

            if (p.FileSystem is { } fs && IsFat(fs))
            {
                var failed = Fits(plan, p);
                if (failed is not null)
                {
                    yield return $"partition {i}: {failed}";
                }
            }
        }

        if (plan.Superfloppy)
        {
            if (plan.Scheme != PartitionScheme.Auto || parts.Count > 1 || (parts.Count == 1 && parts[0].StartBytes != 0))
            {
                yield return "superfloppy layout is wrong";
            }
        }
        else if (plan.WriteMethod != WriteMethod.RawCopy)
        {
            if (parts.Count == 0 || plan.Scheme is not (PartitionScheme.Mbr or PartitionScheme.Gpt))
            {
                yield return "a formatted plan needs partitions and a scheme";
            }

            if (parts.Count > 0 && parts[0].StartBytes < 63 * 512)
            {
                yield return "first partition starts inside the MBR gap";
            }
        }

        if (plan.Scheme == PartitionScheme.Mbr)
        {
            if (parts.Count > 4 || parts.Count(p => p.Active) > 1)
            {
                yield return "too many MBR entries or boot flags";
            }

            if (parts.Any(p => p.MbrType == 0 && p.Role != PartitionRole.Main))
            {
                yield return "MBR partition without a type";
            }

            if (parts.Any(p => p.EndBytes > uint.MaxValue * (long)ss))
            {
                yield return "MBR partition beyond 32-bit LBA";
            }
        }

        if (plan.Scheme == PartitionScheme.Gpt)
        {
            var tail = 1 + (16384 + ss - 1) / ss;
            if (parts.Any(p => p.StartBytes < 34 * 512 || p.EndBytes > plan.DeviceBytes - (tail * ss)))
            {
                yield return "GPT partition outside the usable range";
            }
        }

        if (plan.Scheme != PartitionScheme.Auto)
        {
            var failure = LayoutWrites(plan);
            if (failure is not null)
            {
                yield return failure;
            }
        }

        var helper = parts.Any(p => p.Role == PartitionRole.UefiNtfs);
        if (plan.UsesUefiNtfs != helper || plan.UsesUefiNtfs != plan.BootMethod.HasFlag(BootMethod.UefiNtfs))
        {
            yield return "UEFI:NTFS flag, partition and boot method disagree";
        }

        if (plan.NeedsPersistencePartition != parts.Any(p => p.Role == PartitionRole.Persistence))
        {
            yield return "persistence flag and partition disagree";
        }

        if (plan.SplitWim && (plan.Partitions.All(p => p.FileSystem is not FileSystemKind.Fat32) || !image.HasFileOver4GiB))
        {
            yield return "split WIM without need or without FAT32";
        }

        var needsHelper = plan.WriteMethod == WriteMethod.ExtractFiles && !plan.Superfloppy
            && image.Kind is ImageKind.WindowsSetup or ImageKind.LinuxIsoOnly or ImageKind.LinuxHybrid
            && parts.Any(p => p.Role == PartitionRole.Main && p.FileSystem is FileSystemKind.Ntfs or FileSystemKind.ExFat)
            && plan.Firmware is TargetFirmware.Uefi or TargetFirmware.BiosAndUefi;
        if (needsHelper != plan.UsesUefiNtfs)
        {
            yield return "UEFI media on NTFS or exFAT need exactly the helper partition";
        }

        foreach (var warning in plan.Warnings)
        {
            if (!PlanWarningCodeList.All.Contains(warning.Code))
            {
                yield return $"unknown warning {warning.Code}";
            }
        }

        if (plan.WriteMethod == WriteMethod.RawCopy && (plan.BootMethod != BootMethod.ImageNative || plan.Scheme != PartitionScheme.Auto))
        {
            yield return "raw copy must keep the image's own table and boot code";
        }
    }

    private static bool IsFat(FileSystemKind fileSystem) => fileSystem is FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32;

    private static string? Fits(MediaPlan plan, PlannedPartition partition)
    {
        try
        {
            FatGeometry.Compute(plan.ToFatOptions(partition));
            return null;
        }
        catch (BootrixException ex)
        {
            return $"FAT geometry is infeasible: {ex.Detail}";
        }
    }

    private static string? LayoutWrites(MediaPlan plan)
    {
        try
        {
            var layout = plan.ToDiskLayout(mbrSignature: 1);
            var disk = new SparseMemoryStream(plan.DeviceBytes);
            DiskLayoutWriter.WriteToStream(disk, layout, plan.SectorSize);

            var sector = new byte[plan.SectorSize];
            disk.Position = 0;
            disk.ReadExactly(sector);
            if (!Mbr.TryParse(sector, out var mbr))
            {
                return "written layout has no MBR signature";
            }

            if (plan.Scheme == PartitionScheme.Gpt)
            {
                var gpt = Gpt.Read(disk, plan.SectorSize);
                return gpt is null || gpt.Partitions.Count != plan.Partitions.Count || !mbr.IsProtective
                    ? "written GPT cannot be read back"
                    : null;
            }

            return mbr.Entries.Count(e => !e.IsEmpty) != plan.Partitions.Count ? "written MBR lost partitions" : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return $"layout cannot be written: {ex.Message}";
        }
    }
}

internal static class PlanWarningCodeList
{
    public static readonly HashSet<string> All = typeof(PlanWarningCodes)
        .GetFields()
        .Where(f => f.IsLiteral)
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.Ordinal);
}
