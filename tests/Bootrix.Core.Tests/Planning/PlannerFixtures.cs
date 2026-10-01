// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Tests.Planning;

internal static class PlannerFixtures
{
    public const long Kib = 1024;
    public const long Mib = 1024 * Kib;
    public const long Gib = 1024 * Mib;
    public const long Tib = 1024 * Gib;

    public static ImageProfile WindowsIso(bool bigWim = false, WindowsArch arch = WindowsArch.X64) => new()
    {
        Kind = ImageKind.WindowsSetup,
        VolumeLabel = "CCCOMA_X64FRE_DE-DE_DV9",
        TotalBytes = bigWim ? 6 * Gib : 4 * Gib - 200 * Mib,
        LargestFileBytes = bigWim ? 5 * Gib : 3 * Gib + 700 * Mib,
        HasBiosBootFiles = arch != WindowsArch.Arm64,
        HasEfiBootFiles = true,
        Arch = arch,
        WindowsBuild = 26200,
    };

    public static ImageProfile LinuxHybrid(string family = "debian", long total = 3 * Gib) => new()
    {
        Kind = ImageKind.LinuxHybrid,
        Family = family,
        VolumeLabel = "DEBIAN_LIVE",
        TotalBytes = total,
        LargestFileBytes = total / 2,
        IsHybrid = true,
        HasBiosBootFiles = true,
        HasEfiBootFiles = true,
        HasElToritoBios = true,
        HasElToritoEfi = true,
    };

    public static ImageProfile LinuxIso(string family = "arch", long total = 1 * Gib, bool big = false) => new()
    {
        Kind = ImageKind.LinuxIsoOnly,
        Family = family,
        VolumeLabel = "ARCH_LIVE",
        TotalBytes = big ? 6 * Gib : total,
        LargestFileBytes = big ? 5 * Gib : total / 2,
        HasBiosBootFiles = true,
        HasEfiBootFiles = true,
        HasElToritoBios = true,
    };

    public static ImageProfile Dos() => new() { Kind = ImageKind.Dos };

    public static ImageProfile Data(long total = 100 * Mib, bool big = false) => new()
    {
        Kind = ImageKind.Data,
        TotalBytes = big ? 6 * Gib : total,
        LargestFileBytes = big ? 5 * Gib : total / 2,
    };

    public static ImageProfile RawDisk(long total = 2 * Gib) => new() { Kind = ImageKind.RawDisk, TotalBytes = total };

    public static ImageProfile Unknown() => new() { Kind = ImageKind.Unknown };

    public static DeviceCaps Stick(long bytes, int sectorSize = 512) => new()
    {
        SizeBytes = bytes,
        LogicalSectorSize = sectorSize,
        PhysicalSectorSize = sectorSize,
        Bus = DeviceBus.Usb,
        Medium = DeviceMedium.Stick,
        Removable = true,
    };

    public static DeviceCaps Floppy(long bytes = 1_474_560) => new()
    {
        SizeBytes = bytes,
        Bus = DeviceBus.Unknown,
        Medium = DeviceMedium.Floppy,
        Removable = true,
    };

    public static ImageProfile ByKey(string key) => key switch
    {
        "win" => WindowsIso(),
        "win-big" => WindowsIso(bigWim: true),
        "win-arm" => WindowsIso(arch: WindowsArch.Arm64),
        "linux-hybrid" => LinuxHybrid(),
        "linux-iso" => LinuxIso(),
        "linux-iso-big" => LinuxIso(big: true),
        "ubuntu-iso" => LinuxIso("ubuntu"),
        "dos" => Dos(),
        "data" => Data(),
        "data-big" => Data(big: true),
        "raw" => RawDisk(),
        "unknown" => Unknown(),
        _ => throw new ArgumentException($"unknown image key {key}", nameof(key)),
    };

    public static string Describe(MediaPlan plan)
    {
        var parts = plan.Partitions.Select(partition => DescribePartition(plan, partition));
        return $"{plan.Scheme}/{plan.WriteMethod}/{plan.BootMethod}|{string.Join(' ', parts)}".TrimEnd('|', ' ');
    }

    private static string DescribePartition(MediaPlan plan, PlannedPartition partition)
    {
        var fileSystem = partition.FileSystem?.ToString() ?? "raw";
        if (plan.Scheme == PartitionScheme.Gpt)
        {
            var type = partition.GptType == GptTypes.EfiSystem ? "esp"
                : partition.GptType == GptTypes.MicrosoftReserved ? "msr"
                : partition.GptType == GptTypes.BasicData ? "basic"
                : partition.GptType == GptTypes.LinuxData ? "linux"
                : partition.GptType == GptTypes.BiosBoot ? "biosboot"
                : "other";
            var attributes = (partition.GptAttributes.HasFlag(GptAttributes.NoDriveLetter) ? "+nodrive" : "")
                + (partition.GptAttributes.HasFlag(GptAttributes.LegacyBiosBootable) ? "+bios" : "");
            return $"{partition.Role}:{fileSystem}:{type}{attributes}";
        }

        return $"{partition.Role}:{fileSystem}:0x{partition.MbrType:X2}{(partition.Active ? "*" : "")}";
    }
}
