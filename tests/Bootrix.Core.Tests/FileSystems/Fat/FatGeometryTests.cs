// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;

namespace Bootrix.Core.Tests.FileSystems.Fat;

public class FatGeometryTests
{
    private const long Kib = 1024;
    private const long Mib = 1024 * Kib;
    private const long Gib = 1024 * Mib;
    private const long Tib = 1024 * Gib;

    [Theory]
    [InlineData(1 * Mib, 512)]
    [InlineData(32 * Mib, 512)]
    [InlineData(32 * Mib + 512, 1024)]
    [InlineData(64 * Mib, 1024)]
    [InlineData(100 * Mib, 2048)]
    [InlineData(256 * Mib, 4096)]
    [InlineData(512 * Mib, 8192)]
    [InlineData(1 * Gib, 16384)]
    [InlineData(2 * Gib, 32768)]
    [InlineData(3 * Gib, 65536)]
    public void DefaultClusterBytes_Fat16_FollowsTheMicrosoftTable(long volume, int expected)
    {
        Assert.Equal(expected, FatGeometry.DefaultClusterBytes(FatType.Fat16, volume));
    }

    [Theory]
    [InlineData(33 * Mib, 512)]
    [InlineData(64 * Mib - 512, 512)]
    [InlineData(64 * Mib, 1024)]
    [InlineData(128 * Mib, 2048)]
    [InlineData(256 * Mib, 4096)]
    [InlineData(8 * Gib - 512, 4096)]
    [InlineData(8 * Gib, 8192)]
    [InlineData(16 * Gib, 16384)]
    [InlineData(32 * Gib - 512, 16384)]
    [InlineData(32 * Gib, 32768)]
    [InlineData(2 * Tib - 512, 32768)]
    [InlineData(2 * Tib, 65536)]
    public void DefaultClusterBytes_Fat32_FollowsTheMicrosoftAndFat32formatTable(long volume, int expected)
    {
        Assert.Equal(expected, FatGeometry.DefaultClusterBytes(FatType.Fat32, volume));
    }

    [Theory]
    [InlineData(2880, 2, 1, 14, 9)]
    [InlineData(1440, 2, 1, 7, 3)]
    [InlineData(2400, 2, 1, 14, 7)]
    [InlineData(5760, 2, 2, 15, 9)]
    public void SectorsPerFat_Fat12_ReproducesTheClassicDisketteValues(long total, int fats, int reserved, long rootSectors, long expected)
    {
        var spc = total is 1440 or 5760 ? 2 : 1;

        Assert.Equal(expected, FatGeometry.SectorsPerFat(FatType.Fat12, total, reserved, rootSectors, spc, 512, fats));
    }

    [Theory]
    [InlineData(FatType.Fat12, 4096, 1)]
    [InlineData(FatType.Fat16, 262144, 4)]
    [InlineData(FatType.Fat16, 2097152, 32)]
    [InlineData(FatType.Fat32, 1048576, 8)]
    [InlineData(FatType.Fat32, 4194304, 8)]
    [InlineData(FatType.Fat32, 67108864, 32)]
    public void SectorsPerFat_IsTheSmallestFatThatCoversAllClusters(FatType type, long total, int spc)
    {
        const int reserved = 32;
        var rootSectors = type == FatType.Fat32 ? 0 : 32;

        var fat = FatGeometry.SectorsPerFat(type, total, reserved, rootSectors, spc, 512, 2);
        var clusters = FatGeometry.CountClusters(total, reserved, 2, fat, rootSectors, spc);

        Assert.True(fat * 512 * 8 >= (clusters + 2) * (int)type, "FAT too small for its clusters");
        var smaller = FatGeometry.CountClusters(total, reserved, 2, fat - 1, rootSectors, spc);
        Assert.True((fat - 1) * 512 * 8 < (smaller + 2) * (int)type, "FAT could be one sector smaller");
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, FatType.Fat12)]
    [InlineData(4084, FatType.Fat12)]
    [InlineData(4085, FatType.Fat16)]
    [InlineData(65524, FatType.Fat16)]
    [InlineData(65525, FatType.Fat32)]
    [InlineData(268435445, FatType.Fat32)]
    public void TypeForClusterCount_SwitchesAtTheSpecifiedThresholds(long clusters, FatType? expected)
    {
        Assert.Equal(expected, FatGeometry.TypeForClusterCount(clusters));
    }

    [Fact]
    public void Compute_Auto_PicksTheTypeByVolumeSize()
    {
        Assert.Equal(FatType.Fat12, Compute(1 * Mib).Type);
        Assert.Equal(FatType.Fat16, Compute(16 * Mib).Type);
        Assert.Equal(FatType.Fat16, Compute(512 * Mib).Type);
        Assert.Equal(FatType.Fat32, Compute(513 * Mib).Type);
        Assert.Equal(FatType.Fat32, Compute(40 * Gib).Type);
    }

    [Fact]
    public void Compute_Fat32_AlignsTheDataAreaToOneMebibyte()
    {
        foreach (var hidden in new uint[] { 0, 63, 2048, 4096 })
        {
            var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = 8 * Gib, Type = FatType.Fat32, HiddenSectors = hidden });

            Assert.Equal(0, (hidden + layout.DataStartSector) % 2048);
            Assert.InRange(layout.ReservedSectors, 32, 32 + 2048);
        }
    }

    [Fact]
    public void Compute_Fat32_WithExplicitReservedSectors_IsLeftAlone()
    {
        var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = 8 * Gib, Type = FatType.Fat32, ReservedSectors = 32 });

        Assert.Equal(32, layout.ReservedSectors);
    }

    [Fact]
    public void Compute_Fat32_FourKibSectors_AlignsInSectors()
    {
        var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = 4 * Gib, BytesPerSector = 4096, Type = FatType.Fat32 });

        Assert.Equal(0, layout.DataStartSector % 256);
        Assert.Equal(1, layout.SectorsPerCluster);
    }

    [Fact]
    public void Compute_ForcedFat16_GrowsClustersUntilTheCountFits()
    {
        var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib + 100 * Mib, Type = FatType.Fat16 });

        Assert.Equal(32 * Kib, layout.ClusterBytes);
        Assert.InRange(layout.ClusterCount, 4085, 65524);
    }

    [Fact]
    public void Compute_ForcedFat16_AboveFourGibibytes_IsRefused()
    {
        var ex = Assert.Throws<BootrixException>(() => Compute(4 * Gib, FatType.Fat16));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void Compute_ForcedFat32_BelowTheClusterMinimum_IsRefused()
    {
        Assert.Throws<BootrixException>(() => Compute(32 * Mib, FatType.Fat32));
        Assert.Equal(FatType.Fat32, Compute(33 * Mib, FatType.Fat32).Type);
    }

    [Fact]
    public void Compute_ForcedFat32_ShrinksClustersForSmallVolumes()
    {
        var layout = Compute(40 * Mib, FatType.Fat32);

        Assert.Equal(1, layout.SectorsPerCluster);
        Assert.True(layout.ClusterCount >= 65525);
    }

    [Fact]
    public void Compute_ExplicitClusterSize_IsNeverAdjusted()
    {
        var options = new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat16, SectorsPerCluster = 4 };

        Assert.Throws<BootrixException>(() => FatGeometry.Compute(options));
    }

    [Fact]
    public void Compute_ExplicitClusterSize_WithoutType_ChoosesTheTypeItsClusterCountImplies()
    {
        Assert.Equal(FatType.Fat12, FatGeometry.Compute(new FatFormatOptions { TotalBytes = 2 * Mib, SectorsPerCluster = 1 }).Type);
        Assert.Equal(FatType.Fat16, FatGeometry.Compute(new FatFormatOptions { TotalBytes = 20 * Mib, SectorsPerCluster = 1 }).Type);
        Assert.Equal(FatType.Fat32, FatGeometry.Compute(new FatFormatOptions { TotalBytes = 200 * Mib, SectorsPerCluster = 1 }).Type);
    }

    [Fact]
    public void Compute_Fat12Fat16Boundary_NeverProducesAnAmbiguousVolume()
    {
        var sawPaddedReservedArea = false;
        for (var total = 4050 + 40; total < 4130 + 40; total++)
        {
            var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = total * 512L, SectorsPerCluster = 1 });

            Assert.Equal(FatGeometry.TypeForClusterCount(layout.ClusterCount), layout.Type);
            sawPaddedReservedArea |= layout is { Type: FatType.Fat12, ReservedSectors: > 1 };
        }

        Assert.True(sawPaddedReservedArea, "the window should include sizes in the FAT12/FAT16 gap");
    }

    [Fact]
    public void Compute_Fat16Fat32Boundary_SwitchesExactlyAtTheThreshold()
    {
        FatLayout? last16 = null;
        FatLayout? first32 = null;
        for (var total = 66000L; total < 66800; total++)
        {
            var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = total * 512, SectorsPerCluster = 1 });
            Assert.Equal(FatGeometry.TypeForClusterCount(layout.ClusterCount), layout.Type);
            if (layout.Type == FatType.Fat16)
            {
                last16 = layout;
            }
            else
            {
                first32 ??= layout;
            }
        }

        Assert.NotNull(last16);
        Assert.NotNull(first32);
        Assert.Equal(65524, last16.ClusterCount);
        Assert.True(first32.ClusterCount >= 65525);
    }

    [Fact]
    public void Compute_FourKibSectors_UsesWholeSectorClusters()
    {
        var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = 64 * Mib, BytesPerSector = 4096 });

        Assert.Equal(FatType.Fat16, layout.Type);
        Assert.Equal(4096, layout.ClusterBytes);
        Assert.Equal(512, layout.RootEntries);
        Assert.Equal(4, layout.RootDirectorySectors);
    }

    [Fact]
    public void Compute_RootEntries_AreRoundedUpToWholeSectors()
    {
        var layout = FatGeometry.Compute(new FatFormatOptions { TotalBytes = 20 * Mib, RootEntries = 100 });

        Assert.Equal(112, layout.RootEntries);
    }

    [Fact]
    public void Compute_Fat32_HasNoFixedRootDirectory()
    {
        var layout = Compute(1 * Gib, FatType.Fat32);

        Assert.Equal(0, layout.RootEntries);
        Assert.Equal(0, layout.RootDirectorySectors);
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = Gib, Type = FatType.Fat32, RootEntries = 512 }));
    }

    [Fact]
    public void Compute_MaxFat32Size_IsTwoTibibytesMinusOneSector()
    {
        Assert.Equal(2 * Tib - 512, FatGeometry.MaxFat32Bytes(512));
        Assert.Equal(16 * Tib - 4096, FatGeometry.MaxFat32Bytes(4096));

        var largest = Compute(FatGeometry.MaxFat32Bytes(512), FatType.Fat32);
        Assert.Equal(32 * Kib, largest.ClusterBytes);
        Assert.Throws<BootrixException>(() => Compute(2 * Tib, FatType.Fat32));
    }

    [Fact]
    public void Compute_SixteenTibibytesOnFourKibSectors_StaysWithinTheFat32ClusterLimit()
    {
        var layout = FatGeometry.Compute(new FatFormatOptions
        {
            TotalBytes = FatGeometry.MaxFat32Bytes(4096),
            BytesPerSector = 4096,
            Type = FatType.Fat32,
        });

        Assert.Equal(65536, layout.ClusterBytes);
        Assert.InRange(layout.ClusterCount, 65525, FatGeometry.MaxFat32Clusters);
    }

    [Fact]
    public void Compute_RejectsNonsense()
    {
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib, BytesPerSector = 520 }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1000 }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib, FatCount = 3 }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib, SectorsPerCluster = 3 }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib, SectorsPerCluster = 256 }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat32, ReservedSectors = 4 }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat32, ReservedSectors = 70_000 }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib, MediaDescriptor = 0x10 }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 1 * Gib, OemName = "TOO LONG NAME" }));
        Assert.Throws<BootrixException>(() => FatGeometry.Compute(new FatFormatOptions { TotalBytes = 4 * Tib }));
    }

    [Fact]
    public void FloppyPresets_AgreeWithTheComputedGeometry()
    {
        foreach (var preset in FloppyPreset.All)
        {
            var layout = FatGeometry.Compute(preset.ToOptions());

            Assert.Equal(FatType.Fat12, layout.Type);
            Assert.Equal(preset.SectorsPerFat, layout.SectorsPerFat);
            Assert.Equal(preset.TotalSectors, layout.TotalSectors);
            Assert.Equal(preset, FloppyPreset.FromSize(preset.TotalBytes));
        }

        Assert.Null(FloppyPreset.FromSize(1234567));
    }

    private static FatLayout Compute(long bytes, FatType? type = null) =>
        FatGeometry.Compute(new FatFormatOptions { TotalBytes = bytes, Type = type });
}
