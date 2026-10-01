// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Planning;

internal sealed record FileSystemChoice(FileSystemKind FileSystem, bool SplitWim);

/// <summary>Which file system a medium gets, what it can hold and which partition type byte goes with it.</summary>
internal static class FileSystemRules
{
    private const long MinOtherBytes = 8 * PlanLimits.Mib;

    public static bool IsFat(FileSystemKind fileSystem) => fileSystem is FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32;

    public static FatType ToFatType(FileSystemKind fileSystem) => fileSystem switch
    {
        FileSystemKind.Fat12 => FatType.Fat12,
        FileSystemKind.Fat16 => FatType.Fat16,
        _ => FatType.Fat32,
    };

    /// <summary>The largest volume the file system takes; FAT16 is 2 GiB for DOS, which cannot use 64 KiB clusters.</summary>
    public static long MaxBytes(FileSystemKind fileSystem, int sectorSize, bool dos) => fileSystem switch
    {
        FileSystemKind.Fat12 => PlanLimits.Fat12MaxBytes,
        FileSystemKind.Fat16 => dos ? PlanLimits.DosFat16MaxBytes : PlanLimits.Fat16MaxBytes,
        FileSystemKind.Fat32 => PartitionAlignment.AlignDown(FatGeometry.MaxFat32Bytes(sectorSize), PlanLimits.Mib),
        _ => long.MaxValue,
    };

    public static bool Fits(FileSystemKind fileSystem, long bytes, int sectorSize, int? clusterBytes)
    {
        if (!IsFat(fileSystem))
        {
            return bytes >= MinOtherBytes;
        }

        try
        {
            _ = FatGeometry.Compute(new FatFormatOptions
            {
                TotalBytes = Math.Min(bytes, MaxBytes(fileSystem, sectorSize, dos: false)),
                BytesPerSector = sectorSize,
                Type = ToFatType(fileSystem),
                SectorsPerCluster = clusterBytes is { } size ? Math.Max(1, size / sectorSize) : null,
            });
            return true;
        }
        catch (BootrixException)
        {
            return false;
        }
    }

    /// <summary>The type FAT picks for a volume of this size when nobody asks for one; the formatter makes the same decision.</summary>
    public static FileSystemKind AutoFat(long bytes, int sectorSize)
    {
        try
        {
            var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = bytes, BytesPerSector = sectorSize });
            return layout.Type switch
            {
                FatType.Fat12 => FileSystemKind.Fat12,
                FatType.Fat16 => FileSystemKind.Fat16,
                _ => FileSystemKind.Fat32,
            };
        }
        catch (BootrixException)
        {
            return FileSystemKind.Fat12;
        }
    }

    /// <summary>
    /// The MBR type byte. The CHS flavours (0x06, 0x0B) matter to MS-DOS and BIOSes that distrust
    /// LBA; they are only valid when the whole partition lies within the CHS-addressable first 8 GB.
    /// </summary>
    public static byte MbrType(FileSystemKind fileSystem, long lengthBytes, long endBytes, bool chsTypes, int sectorSize)
    {
        var reachableByChs = endBytes <= PlanLimits.ChsLimitBytes;
        return fileSystem switch
        {
            FileSystemKind.Fat12 => MbrPartitionType.Fat12,
            FileSystemKind.Fat16 when lengthBytes / sectorSize < 65536 => MbrPartitionType.Fat16Small,
            FileSystemKind.Fat16 => chsTypes && reachableByChs ? MbrPartitionType.Fat16 : MbrPartitionType.Fat16Lba,
            FileSystemKind.Fat32 => chsTypes && reachableByChs ? MbrPartitionType.Fat32Chs : MbrPartitionType.Fat32Lba,
            FileSystemKind.Ext3 => MbrPartitionType.Linux,
            _ => MbrPartitionType.Ntfs,
        };
    }

    public static Guid GptType(FileSystemKind fileSystem) =>
        fileSystem == FileSystemKind.Ext3 ? GptTypes.LinuxData : GptTypes.BasicData;

    /// <summary>Picks the file system for the main partition and checks the explicit choice against what the medium can do.</summary>
    public static FileSystemChoice Choose(PlanContext ctx, long budgetBytes, int? clusterBytes)
    {
        var image = ctx.Image;
        var requested = ctx.Target.FileSystem;
        var big = image.HasFileOver4GiB;
        var dos = ctx.Purpose == MediaPurpose.Dos;

        var fileSystem = requested != FileSystemKind.Auto ? requested : Automatic(ctx, budgetBytes, big);
        if (!Allowed(ctx.Purpose, fileSystem))
        {
            throw PlanContext.Unsupported(ErrorCode.FileSystemUnsupported, fileSystem, image.Kind);
        }

        var split = false;
        if (IsFat(fileSystem) && big)
        {
            if (ctx.Purpose != MediaPurpose.Windows)
            {
                throw PlanContext.Unsupported(ErrorCode.FileTooLargeForFileSystem, fileSystem, SizeText.Format(image.LargestFileBytes));
            }

            // install.wim is the only file Bootrix can cut into pieces that Windows setup puts back together.
            split = true;
        }

        var capped = Math.Min(budgetBytes, MaxBytes(fileSystem, ctx.SectorSize, dos));
        if (!Fits(fileSystem, capped, ctx.SectorSize, clusterBytes))
        {
            if (requested == FileSystemKind.Auto && fileSystem == FileSystemKind.Fat32
                && Fits(FileSystemKind.Fat16, capped, ctx.SectorSize, clusterBytes) && !big && ctx.Purpose != MediaPurpose.Windows)
            {
                return new FileSystemChoice(FileSystemKind.Fat16, false);
            }

            throw PlanContext.Unsupported(ErrorCode.FileSystemTooSmall, fileSystem, SizeText.Format(capped));
        }

        return new FileSystemChoice(fileSystem, split);
    }

    private static FileSystemKind Automatic(PlanContext ctx, long budgetBytes, bool big)
    {
        switch (ctx.Purpose)
        {
            case MediaPurpose.Windows:
                return FileSystemKind.Fat32;
            case MediaPurpose.Linux:
                return big ? FileSystemKind.Ntfs : FileSystemKind.Fat32;
            case MediaPurpose.Dos:
                // FAT16 reaches every DOS; FAT32 only from DOS 7.1 on, so it is used only where FAT16 runs out.
                if (budgetBytes > PlanLimits.DosFat16MaxBytes)
                {
                    return FileSystemKind.Fat32;
                }

                var smallest = AutoFat(budgetBytes, ctx.SectorSize);
                return smallest == FileSystemKind.Fat32 ? FileSystemKind.Fat16 : smallest;
            default:
                if (big || budgetBytes > PlanLimits.Fat32DataLimitBytes)
                {
                    return FileSystemKind.ExFat;
                }

                return budgetBytes <= 512 * PlanLimits.Mib ? AutoFat(budgetBytes, ctx.SectorSize) : FileSystemKind.Fat32;
        }
    }

    private static bool Allowed(MediaPurpose purpose, FileSystemKind fileSystem) => purpose switch
    {
        MediaPurpose.Windows => fileSystem is FileSystemKind.Fat32 or FileSystemKind.Ntfs or FileSystemKind.ExFat,
        MediaPurpose.Linux => fileSystem is FileSystemKind.Fat16 or FileSystemKind.Fat32 or FileSystemKind.Ntfs or FileSystemKind.ExFat,
        MediaPurpose.Dos => IsFat(fileSystem),
        _ => true,
    };
}
