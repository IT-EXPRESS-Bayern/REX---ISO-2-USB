// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.FileSystems.Fat;

/// <summary>
/// The arithmetic behind a FAT volume. Drivers tell the types apart by cluster count alone
/// (below 4085 is FAT12, below 65525 is FAT16), so every choice made here is checked against it.
/// </summary>
public static class FatGeometry
{
    public const int MaxFat12Clusters = 4084;
    public const int MaxFat16Clusters = 65524;

    /// <summary>The top four bits of a FAT32 entry are reserved and 0x0FFFFFF7 and up mark special values.</summary>
    public const long MaxFat32Clusters = 0x0FFFFFF5;

    /// <summary>Some drivers reject clusters above 32 KiB; 64 KiB is the largest value still seen in the wild.</summary>
    public const int MaxClusterBytes = 64 * 1024;

    public const int MaxSectorsPerCluster = 128;

    private const long Kib = 1024;
    private const long Mib = 1024 * Kib;
    private const long Gib = 1024 * Mib;
    private const long Tib = 1024 * Gib;
    private const int FatAlignmentBytes = (int)Mib;
    private const int Fat32DefaultReserved = 32;
    private const int Fat32MinReserved = 9;
    private const int DefaultRootEntries = 512;

    private static readonly FatType[] AllTypes = [FatType.Fat12, FatType.Fat16, FatType.Fat32];
    private static readonly FatType[] SmallVolumeTypes = [FatType.Fat12, FatType.Fat16];

    /// <summary>Cluster size Windows picks for a volume of the given size (Microsoft KB 140365; fat32format above 32 GiB).</summary>
    public static int DefaultClusterBytes(FatType type, long volumeBytes) => type switch
    {
        FatType.Fat12 => 512,
        FatType.Fat16 => volumeBytes switch
        {
            <= 32 * Mib => 512,
            <= 64 * Mib => (int)Kib,
            <= 128 * Mib => (int)(2 * Kib),
            <= 256 * Mib => (int)(4 * Kib),
            <= 512 * Mib => (int)(8 * Kib),
            <= Gib => (int)(16 * Kib),
            <= 2 * Gib => (int)(32 * Kib),
            _ => (int)(64 * Kib),
        },
        _ => volumeBytes switch
        {
            < 64 * Mib => 512,
            < 128 * Mib => (int)Kib,
            < 256 * Mib => (int)(2 * Kib),
            < 8 * Gib => (int)(4 * Kib),
            < 16 * Gib => (int)(8 * Kib),
            < 32 * Gib => (int)(16 * Kib),
            < 2 * Tib => (int)(32 * Kib),
            _ => (int)(64 * Kib),
        },
    };

    /// <summary>
    /// Smallest FAT, in sectors, that can address every cluster of the data area it leaves behind.
    /// Solves FatBytes * 8 >= (clusters + 2) * bits with clusters = (total - reserved - root - fats * FatSectors) / sectorsPerCluster.
    /// </summary>
    public static long SectorsPerFat(
        FatType type, long totalSectors, int reservedSectors, long rootDirectorySectors,
        int sectorsPerCluster, int bytesPerSector, int fatCount)
    {
        long bits = (int)type;
        var numerator = bits * (totalSectors - reservedSectors - rootDirectorySectors + 2L * sectorsPerCluster);
        var denominator = 8L * bytesPerSector * sectorsPerCluster + bits * fatCount;
        return Math.Max(1, (numerator + denominator - 1) / denominator);
    }

    public static long CountClusters(
        long totalSectors, int reservedSectors, int fatCount, long sectorsPerFat,
        long rootDirectorySectors, int sectorsPerCluster)
    {
        var dataSectors = totalSectors - reservedSectors - fatCount * sectorsPerFat - rootDirectorySectors;
        return dataSectors <= 0 ? 0 : dataSectors / sectorsPerCluster;
    }

    public static bool FitsType(FatType type, long clusterCount) => type switch
    {
        FatType.Fat12 => clusterCount is >= 1 and <= MaxFat12Clusters,
        FatType.Fat16 => clusterCount is > MaxFat12Clusters and <= MaxFat16Clusters,
        _ => clusterCount is > MaxFat16Clusters and <= MaxFat32Clusters,
    };

    /// <summary>The FAT type a cluster count defines, or null when it is zero.</summary>
    public static FatType? TypeForClusterCount(long clusterCount) => clusterCount switch
    {
        < 1 => null,
        <= MaxFat12Clusters => FatType.Fat12,
        <= MaxFat16Clusters => FatType.Fat16,
        _ => FatType.Fat32,
    };

    /// <summary>Largest volume FAT32 can describe: its total sector field is 32 bits wide.</summary>
    public static long MaxFat32Bytes(int bytesPerSector) => uint.MaxValue * (long)bytesPerSector;

    public static FatLayout Compute(FatFormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var totalSectors = options.TotalBytes / options.BytesPerSector;
        if (totalSectors > uint.MaxValue)
        {
            throw Invalid($"{options.TotalBytes} bytes exceed the 32-bit sector count of FAT");
        }

        if (options.Type is { } forced)
        {
            return ResolveForced(forced, options, totalSectors);
        }

        return options.SectorsPerCluster is { } given
            ? ResolveByClusterSize(options, totalSectors, given)
            : ResolveBySize(options, totalSectors);
    }

    private static FatLayout ResolveForced(FatType type, FatFormatOptions options, long totalSectors)
    {
        if (options.SectorsPerCluster is { } given)
        {
            var layout = Build(type, options, totalSectors, given);
            return FitsType(type, layout.ClusterCount)
                ? layout
                : throw Invalid($"{type} with {layout.ClusterBytes}-byte clusters would have {layout.ClusterCount} clusters");
        }

        var spc = ClusterSectors(options, DefaultClusterBytes(type, options.TotalBytes));
        var candidate = Build(type, options, totalSectors, spc);

        // Too many clusters: grow them. Too few: shrink them, down to one sector.
        while (candidate.ClusterCount > MaxClusterCountFor(type)
            && candidate.SectorsPerCluster * 2 * options.BytesPerSector <= MaxClusterBytes
            && candidate.SectorsPerCluster * 2 <= MaxSectorsPerCluster)
        {
            candidate = Build(type, options, totalSectors, candidate.SectorsPerCluster * 2);
        }

        while (candidate.ClusterCount < MinClusterCountFor(type) && candidate.SectorsPerCluster > 1)
        {
            candidate = Build(type, options, totalSectors, candidate.SectorsPerCluster / 2);
        }

        return FitsType(type, candidate.ClusterCount)
            ? candidate
            : throw Invalid($"{options.TotalBytes} bytes cannot be formatted as {type}: {candidate.ClusterCount} clusters of {candidate.ClusterBytes} bytes");
    }

    private static FatLayout ResolveByClusterSize(FatFormatOptions options, long totalSectors, int sectorsPerCluster)
    {
        foreach (var type in AllTypes)
        {
            var layout = BuildFitting(type, options, totalSectors, sectorsPerCluster);
            if (FitsType(type, layout.ClusterCount))
            {
                return layout;
            }
        }

        throw Invalid($"{sectorsPerCluster} sectors per cluster fit no FAT type on {options.TotalBytes} bytes");
    }

    private static FatLayout ResolveBySize(FatFormatOptions options, long totalSectors)
    {
        // FAT16 stays attractive up to 512 MiB; the table rows beyond that exist only for forced FAT16.
        if (options.TotalBytes <= 512 * Mib)
        {
            foreach (var type in SmallVolumeTypes)
            {
                var spc = ClusterSectors(options, DefaultClusterBytes(type, options.TotalBytes));
                var layout = BuildFitting(type, options, totalSectors, spc);
                if (FitsType(type, layout.ClusterCount))
                {
                    return layout;
                }
            }
        }

        return ResolveForced(FatType.Fat32, options, totalSectors);
    }

    /// <summary>
    /// Near a type boundary the FAT sizes get in each other's way: a volume whose FAT12 count lands
    /// just above 4084 would read as FAT16, yet laid out as FAT16 (a bigger FAT) it falls just below
    /// 4085. The same gap sits between FAT16 and FAT32. Such sizes exist, so the smaller type gives up
    /// a few sectors to the reserved area until its count is unambiguous.
    /// </summary>
    private static FatLayout BuildFitting(FatType type, FatFormatOptions options, long totalSectors, int sectorsPerCluster)
    {
        var layout = Build(type, options, totalSectors, sectorsPerCluster);
        var max = MaxClusterCountFor(type);
        if (type == FatType.Fat32 || options.ReservedSectors is not null || layout.ClusterCount <= max)
        {
            return layout;
        }

        var next = type == FatType.Fat12 ? FatType.Fat16 : FatType.Fat32;
        if (Build(next, options, totalSectors, sectorsPerCluster).ClusterCount >= MinClusterCountFor(next))
        {
            return layout;
        }

        // Each reserved sector costs a data sector; the FAT shrinking by a sector now and then gives a few back.
        var spare = (layout.ClusterCount - max + 1) * sectorsPerCluster + 4 * options.FatCount;
        for (var extra = 1; extra <= spare; extra++)
        {
            var padded = Assemble(type, options, totalSectors, sectorsPerCluster, 1 + extra,
                layout.RootEntries, layout.RootDirectorySectors);
            if (FitsType(type, padded.ClusterCount))
            {
                return padded;
            }
        }

        return layout;
    }

    private static FatLayout Build(FatType type, FatFormatOptions options, long totalSectors, int sectorsPerCluster)
    {
        var bps = options.BytesPerSector;
        var rootEntries = type == FatType.Fat32 ? 0 : RoundRootEntries(options.RootEntries ?? DefaultRootEntries, bps);
        var rootSectors = (long)rootEntries * 32 / bps;

        if (type != FatType.Fat32 || options.ReservedSectors is not null)
        {
            var reserved = options.ReservedSectors ?? 1;
            return Assemble(type, options, totalSectors, sectorsPerCluster, reserved, rootEntries, rootSectors);
        }

        var plain = Assemble(type, options, totalSectors, sectorsPerCluster, Fat32DefaultReserved, rootEntries, rootSectors);
        var aligned = AlignDataArea(options, totalSectors, sectorsPerCluster, rootEntries, rootSectors);

        // Alignment is a nicety for flash erase blocks; never let it cost the volume its FAT32 status.
        return aligned is not null && FitsType(type, aligned.ClusterCount) ? aligned : plain;
    }

    private static FatLayout? AlignDataArea(
        FatFormatOptions options, long totalSectors, int sectorsPerCluster, int rootEntries, long rootSectors)
    {
        var alignSectors = Math.Max(1, FatAlignmentBytes / options.BytesPerSector);
        for (var reserved = Fat32DefaultReserved; reserved < Fat32DefaultReserved + alignSectors; reserved++)
        {
            var fatSectors = SectorsPerFat(FatType.Fat32, totalSectors, reserved, rootSectors,
                sectorsPerCluster, options.BytesPerSector, options.FatCount);
            if ((options.HiddenSectors + reserved + options.FatCount * fatSectors) % alignSectors == 0)
            {
                return Assemble(FatType.Fat32, options, totalSectors, sectorsPerCluster, reserved, rootEntries, rootSectors);
            }
        }

        return null;
    }

    private static FatLayout Assemble(
        FatType type, FatFormatOptions options, long totalSectors, int sectorsPerCluster,
        int reservedSectors, int rootEntries, long rootSectors)
    {
        var fatSectors = SectorsPerFat(type, totalSectors, reservedSectors, rootSectors,
            sectorsPerCluster, options.BytesPerSector, options.FatCount);

        return new FatLayout
        {
            Type = type,
            BytesPerSector = options.BytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            ReservedSectors = reservedSectors,
            FatCount = options.FatCount,
            RootEntries = rootEntries,
            TotalSectors = totalSectors,
            SectorsPerFat = fatSectors,
            ClusterCount = CountClusters(totalSectors, reservedSectors, options.FatCount, fatSectors, rootSectors, sectorsPerCluster),
        };
    }

    private static long MaxClusterCountFor(FatType type) => type switch
    {
        FatType.Fat12 => MaxFat12Clusters,
        FatType.Fat16 => MaxFat16Clusters,
        _ => MaxFat32Clusters,
    };

    private static long MinClusterCountFor(FatType type) => type switch
    {
        FatType.Fat12 => 1,
        FatType.Fat16 => MaxFat12Clusters + 1,
        _ => MaxFat16Clusters + 1,
    };

    private static int ClusterSectors(FatFormatOptions options, int clusterBytes) =>
        Math.Max(1, clusterBytes / options.BytesPerSector);

    /// <summary>Rounds up so the root directory fills whole sectors, as the on-disk format requires.</summary>
    private static int RoundRootEntries(int entries, int bytesPerSector)
    {
        var perSector = bytesPerSector / 32;
        return (entries + perSector - 1) / perSector * perSector;
    }

    private static void Validate(FatFormatOptions options)
    {
        if (options.BytesPerSector is not (512 or 1024 or 2048 or 4096))
        {
            throw Invalid($"unsupported sector size {options.BytesPerSector}");
        }

        if (options.TotalBytes < options.BytesPerSector * 8L)
        {
            throw Invalid($"{options.TotalBytes} bytes are too small for a FAT volume");
        }

        if (options.FatCount is < 1 or > 2)
        {
            throw Invalid($"FAT count {options.FatCount} is not 1 or 2");
        }

        if (options.SectorsPerCluster is { } spc)
        {
            if (spc is < 1 or > MaxSectorsPerCluster || !int.IsPow2(spc))
            {
                throw Invalid($"{spc} sectors per cluster is not a power of two up to {MaxSectorsPerCluster}");
            }

            if ((long)spc * options.BytesPerSector > MaxClusterBytes)
            {
                throw Invalid($"clusters of {(long)spc * options.BytesPerSector} bytes exceed {MaxClusterBytes}");
            }
        }

        var minReserved = options.Type == FatType.Fat32 ? Fat32MinReserved : 1;
        if (options.ReservedSectors is { } reserved && (reserved < minReserved || reserved > ushort.MaxValue))
        {
            throw Invalid($"{reserved} reserved sectors are outside {minReserved} to {ushort.MaxValue}");
        }

        if (options.Type == FatType.Fat32 && options.RootEntries is > 0)
        {
            throw Invalid("FAT32 has no fixed root directory");
        }

        if (options.RootEntries is < 1 && options.Type != FatType.Fat32)
        {
            throw Invalid("the root directory needs at least one entry");
        }

        if (options.OemName.Length > 8 || options.OemName.Any(ch => ch is < ' ' or > '~'))
        {
            throw Invalid($"OEM name '{options.OemName}' is not up to 8 printable ASCII characters");
        }

        if (options.MediaDescriptor < 0xF0)
        {
            throw Invalid($"media descriptor 0x{options.MediaDescriptor:X2} is below 0xF0");
        }

        if (options.SectorsPerTrack is < 1 or > ushort.MaxValue || options.Heads is < 1 or > ushort.MaxValue)
        {
            throw Invalid("sectors per track and heads must fit 16 bits");
        }
    }

    private static BootrixException Invalid(string detail) =>
        new(ErrorCode.InvalidSpec, detail) { Arguments = [detail] };
}
