// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.IO;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.FileSystems.Fat;

/// <summary>Formats real image files and lets fsck.vfat and mtools judge the result.</summary>
public class FatFormatterToolTests
{
    private const long Mib = 1024 * 1024;
    private const long Gib = 1024 * Mib;
    private const long Tib = 1024 * Gib;

    // name, bytes, bytes per sector, forced type (0 = automatic), sectors per cluster (0 = automatic)
    public static TheoryData<string, long, int, int, int> SizeMatrix => new()
    {
        { "tiny-fat12-auto", 1 * Mib, 512, 0, 0 },
        { "2mib-auto", 2 * Mib, 512, 0, 0 },
        { "8mib-fat16-auto", 8 * Mib, 512, 0, 0 },
        { "8mib-fat12-forced", 8 * Mib, 512, 12, 0 },
        { "32mib-fat16", 32 * Mib, 512, 16, 0 },
        { "64mib-fat16", 64 * Mib, 512, 16, 0 },
        { "64mib-auto", 64 * Mib, 512, 0, 0 },
        { "100mib-fat16", 100 * Mib, 512, 16, 0 },
        { "128mib-fat16", 128 * Mib, 512, 16, 0 },
        { "256mib-fat16", 256 * Mib, 512, 16, 0 },
        { "256mib-fat32", 256 * Mib, 512, 32, 0 },
        { "512mib-auto", 512 * Mib, 512, 0, 0 },
        { "512mib-fat32", 512 * Mib, 512, 32, 0 },
        { "1gib-fat16-forced", Gib, 512, 16, 0 },
        { "1gib-auto-fat32", Gib, 512, 0, 0 },
        { "2gib-fat16-forced", 2 * Gib, 512, 16, 0 },
        { "4000mib-fat16-forced", 4000 * Mib, 512, 16, 0 },
        { "33mib-fat32-smallest", 33 * Mib, 512, 32, 0 },
        { "64mib-fat32", 64 * Mib, 512, 32, 0 },
        { "8gib-fat32", 8 * Gib, 512, 0, 0 },
        { "8gib-minus-fat32", (8 * Gib) - (64 * Mib), 512, 0, 0 },
        { "16gib-fat32", 16 * Gib, 512, 0, 0 },
        { "32gib-fat32", 32 * Gib, 512, 0, 0 },
        { "33gib-fat32", 33 * Gib, 512, 0, 0 },
        { "512gib-fat32", 512 * Gib, 512, 0, 0 },
        { "fat32-8-sectors-per-cluster", Gib, 512, 32, 8 },
        { "4k-64mib-auto", 64 * Mib, 4096, 0, 0 },
        { "4k-512mib-fat16", 512 * Mib, 4096, 16, 0 },
        { "4k-512mib-fat32", 512 * Mib, 4096, 32, 0 },
        { "4k-8gib-fat32", 8 * Gib, 4096, 32, 0 },
        { "4k-64gib-fat32", 64 * Gib, 4096, 0, 0 },
    };

    [RequiresToolTheory("fsck.vfat", "mcopy", "mdir")]
    [MemberData(nameof(SizeMatrix))]
    public void Format_Creates_Volume_That_Fsck_And_Mtools_Accept(string name, long bytes, int bytesPerSector, int type, int sectorsPerCluster)
    {
        Assert.NotNull(name);
        using var image = new TempImage(bytes);

        FatFormatResult result;
        using (var stream = image.Open())
        {
            result = FatFormatter.Format(stream, new FatFormatOptions
            {
                TotalBytes = bytes,
                BytesPerSector = bytesPerSector,
                Type = type == 0 ? null : (FatType)type,
                SectorsPerCluster = sectorsPerCluster == 0 ? null : sectorsPerCluster,
                Label = "Bootrix Test",
                AssumeZeroed = true,
            });
        }

        var report = FatVerifier.Fsck(image.Path);
        Assert.Equal(result.Layout.ClusterCount, report.DataClusters);
        Assert.Equal("BOOTRIX TES", FatVerifier.MtoolsLabel(image.Path));

        FatVerifier.MtoolsRoundTrip(image.Path);
        FatVerifier.Fsck(image.Path);
        FatVerifier.DiscUtilsAgrees(image.Path, result.Layout);
    }

    [RequiresToolFact("fsck.vfat", "mcopy", "mdir")]
    public void Format_AtTheLargestFat32Size_Passes_And_OneSectorMoreIsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var largest = FatGeometry.MaxFat32Bytes(512);
        using var image = new TempImage(largest);
        FatFormatResult result;
        using (var stream = image.Open())
        {
            result = FatFormatter.Format(stream, new FatFormatOptions
            {
                TotalBytes = largest,
                Label = "TWO TIB",
                AssumeZeroed = true,
            });
        }

        Assert.Equal(FatType.Fat32, result.Layout.Type);
        Assert.Equal(32 * 1024, result.Layout.ClusterBytes);

        var report = FatVerifier.Fsck(image.Path);
        Assert.Equal(result.Layout.ClusterCount, report.DataClusters);
        FatVerifier.MtoolsRoundTrip(image.Path);
    }

    [RequiresToolFact("fsck.vfat")]
    public void Format_FourKibSectorsBeyondTwoTib_Passes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const long bytes = 3 * Tib;
        using var image = new TempImage(bytes);
        FatFormatResult result;
        using (var stream = image.Open())
        {
            result = FatFormatter.Format(stream, new FatFormatOptions
            {
                TotalBytes = bytes,
                BytesPerSector = 4096,
                AssumeZeroed = true,
            });
        }

        Assert.Equal(FatType.Fat32, result.Layout.Type);
        Assert.Equal(result.Layout.ClusterCount, FatVerifier.Fsck(image.Path).DataClusters);
    }

    [RequiresToolTheory("fsck.vfat", "mcopy", "mdir")]
    [InlineData("160K")]
    [InlineData("180K")]
    [InlineData("320K")]
    [InlineData("360K")]
    [InlineData("720K")]
    [InlineData("1.2M")]
    [InlineData("1.44M")]
    [InlineData("2.88M")]
    public void Format_FloppyPreset_Creates_Standard_Diskette(string name)
    {
        var preset = FloppyPreset.All.Single(p => p.Name == name);
        using var image = new TempImage(preset.TotalBytes);

        FatFormatResult result;
        using (var stream = image.Open())
        {
            result = FatFormatter.Format(stream, preset.ToOptions() with { Label = "FLOPPY" });
        }

        Assert.Equal(FatType.Fat12, result.Layout.Type);
        Assert.Equal(preset.SectorsPerFat, result.Layout.SectorsPerFat);
        Assert.Equal(result.Layout.ClusterCount, FatVerifier.Fsck(image.Path).DataClusters);
        FatVerifier.MtoolsRoundTrip(image.Path, payloadBytes: (int)Math.Min(100_000, preset.TotalBytes / 4));
    }

    [RequiresToolTheory("fsck.vfat", "mcopy", "mdir")]
    [InlineData(32, 1 * Mib)]
    [InlineData(16, 1 * Mib)]
    [InlineData(32, 63 * 512)]
    [InlineData(16, 63 * 512)]
    public void Format_InsideAPartitionOffset_WorksThroughASlice(int type, long partitionOffset)
    {
        const long volumeBytes = 100 * Mib;
        using var image = new TempImage(partitionOffset + volumeBytes + Mib);

        FatFormatResult result;
        using (var disk = image.Open())
        {
            // Garbage in front must survive, garbage inside must not matter.
            disk.Position = 0;
            disk.Write(Enumerable.Repeat((byte)0xA5, 4096).ToArray());
            using var slice = new StreamSlice(disk, partitionOffset, volumeBytes);
            result = FatFormatter.Format(slice, new FatFormatOptions
            {
                TotalBytes = volumeBytes,
                Type = (FatType)type,
                HiddenSectors = (uint)(partitionOffset / 512),
                AssumeZeroed = true,
                Label = "SLICE",
            });
        }

        using (var disk = image.Open())
        {
            var head = new byte[4];
            disk.ReadExactly(head);
            Assert.All(head, b => Assert.Equal(0xA5, b));
        }

        var volume = ExtractVolume(image, partitionOffset, volumeBytes);
        using (volume)
        {
            Assert.Equal(result.Layout.ClusterCount, FatVerifier.Fsck(volume.Path).DataClusters);
        }

        FatVerifier.MtoolsRoundTrip(image.Path, partitionOffset);
    }

    [RequiresToolTheory("fsck.vfat")]
    [InlineData(4040, 4140)]
    [InlineData(66000, 66800)]
    public void Format_EverySizeAroundATypeBoundary_PassesFsck(int firstSector, int lastSector)
    {
        using var image = new TempImage(lastSector * 512L);
        for (var sectors = firstSector; sectors < lastSector; sectors++)
        {
            FatFormatResult result;
            using (var stream = image.Open())
            {
                result = FatFormatter.Format(stream, new FatFormatOptions
                {
                    TotalBytes = sectors * 512L,
                    SectorsPerCluster = 1,
                    Label = "EDGE",
                });
            }

            var report = FatVerifier.Fsck(image.Path);
            Assert.True(result.Layout.ClusterCount == report.DataClusters, $"{sectors} sectors: {result.Layout.ClusterCount} vs {report.DataClusters} clusters");
        }
    }

    [RequiresToolFact("fsck.vfat")]
    public void Format_OverGarbage_ClearsAllMetadata()
    {
        const long bytes = 64 * Mib;
        foreach (var type in new[] { FatType.Fat16, FatType.Fat32 })
        {
            using var image = new TempImage(bytes);
            using (var stream = image.Open())
            {
                var junk = new byte[1 << 20];
                Array.Fill(junk, (byte)0xE5);
                for (var offset = 0L; offset < bytes; offset += junk.Length)
                {
                    stream.Write(junk);
                }

                FatFormatter.Format(stream, new FatFormatOptions { TotalBytes = bytes, Type = type, Label = "CLEAN" });
            }

            FatVerifier.Fsck(image.Path);
        }
    }

    [RequiresToolFact("fsck.vfat")]
    public void Format_WithoutLabel_StoresNoNameAndNoDirectoryEntry()
    {
        using var image = new TempImage(32 * Mib);
        using (var stream = image.Open())
        {
            FatFormatter.Format(stream, new FatFormatOptions { TotalBytes = 32 * Mib, AssumeZeroed = true });
        }

        FatVerifier.Fsck(image.Path);
        Assert.Equal("", FatVerifier.MtoolsLabel(image.Path));
        Assert.Equal("NO NAME", FatVerifier.Minfo(image.Path)["disk label"]);
    }

    [RequiresToolTheory("fsck.vfat", "minfo")]
    [InlineData(16, 512, 63, 255, 0u)]
    [InlineData(32, 512, 32, 64, 2048u)]
    [InlineData(32, 4096, 63, 255, 256u)]
    [InlineData(12, 512, 18, 2, 0u)]
    public void Format_BootSectorFields_MatchWhatMtoolsReadsBack(int type, int bytesPerSector, int sectorsPerTrack, int heads, uint hidden)
    {
        const long bytes = 300 * Mib;
        using var image = new TempImage(bytes);
        FatFormatResult result;
        using (var stream = image.Open())
        {
            result = FatFormatter.Format(stream, new FatFormatOptions
            {
                TotalBytes = bytes,
                BytesPerSector = bytesPerSector,
                Type = type == 12 ? null : (FatType)type,
                SectorsPerCluster = type == 12 ? 128 / (bytesPerSector / 512) : null,
                SectorsPerTrack = sectorsPerTrack,
                Heads = heads,
                HiddenSectors = hidden,
                Label = "Fields",
                OemName = "BOOTRIX",
                VolumeId = 0xCAFE1234,
                DriveNumber = 0x00,
                AssumeZeroed = true,
            });
        }

        var fields = FatVerifier.Minfo(image.Path);
        Assert.Equal("BOOTRIX", fields["banner"]);
        Assert.Equal(bytesPerSector.ToString(CultureInfo.InvariantCulture), fields["sector size"].Split(' ')[0]);
        Assert.Equal(result.Layout.SectorsPerCluster.ToString(CultureInfo.InvariantCulture), fields["cluster size"].Split(' ')[0]);
        Assert.Equal(result.Layout.ReservedSectors.ToString(CultureInfo.InvariantCulture), fields["reserved (boot) sectors"]);
        var fatSectors = result.Layout.Type == FatType.Fat32 ? fields["Big fatlen"] : fields["sectors per fat"];
        Assert.Equal(result.Layout.SectorsPerFat.ToString(CultureInfo.InvariantCulture), fatSectors);
        Assert.Equal(sectorsPerTrack.ToString(CultureInfo.InvariantCulture), fields["sectors per track"]);
        Assert.Equal(heads.ToString(CultureInfo.InvariantCulture), fields["heads"]);
        Assert.Equal(hidden.ToString(CultureInfo.InvariantCulture), fields["hidden sectors"]);
        Assert.Equal("0xf8", fields["media descriptor byte"]);
        Assert.Equal("0x0", fields["physical drive id"]);
        Assert.Equal("CAFE1234", fields["serial number"]);
        Assert.Equal("FIELDS", fields["disk label"]);
        Assert.Equal($"FAT{(int)result.Layout.Type}", fields["disk type"]);

        if (result.Layout.Type == FatType.Fat32)
        {
            Assert.Equal("2", fields["rootCluster"]);
            Assert.Equal("1", fields["infoSector location"]);
            Assert.Equal("6", fields["backup boot sector"]);
            Assert.Equal((result.Layout.ClusterCount - 1).ToString(CultureInfo.InvariantCulture), fields["free clusters"]);
        }
    }

    [RequiresToolFact("fsck.vfat")]
    public void Format_Fat32WithLargeReservedArea_Passes()
    {
        using var image = new TempImage(256 * Mib);
        using (var stream = image.Open())
        {
            FatFormatter.Format(stream, new FatFormatOptions
            {
                TotalBytes = 256 * Mib,
                Type = FatType.Fat32,
                ReservedSectors = 4096,
                FatCount = 1,
                AssumeZeroed = true,
            });
        }

        FatVerifier.Fsck(image.Path);
    }

    [RequiresToolTheory("fsck.vfat")]
    [InlineData(16, 512, 512)]
    [InlineData(32, 512, 512)]
    [InlineData(32, 512, 1536)]
    [InlineData(32, 4096, 4096)]
    public void Format_WithForeignBootCode_StaysAValidVolume(int type, int bytesPerSector, int bootCodeBytes)
    {
        const long bytes = 300 * Mib;
        var code = new byte[bootCodeBytes];
        new Random(5).NextBytes(code);
        code[0] = 0xEB;
        code[1] = type == 32 ? (byte)0x58 : (byte)0x3C;
        code[2] = 0x90;

        using var image = new TempImage(bytes);
        using (var stream = image.Open())
        {
            FatFormatter.Format(stream, new FatFormatOptions
            {
                TotalBytes = bytes,
                BytesPerSector = bytesPerSector,
                Type = (FatType)type,
                BootCode = code,
                Label = "BOOTCODE",
                AssumeZeroed = true,
            });
        }

        FatVerifier.Fsck(image.Path);
        FatVerifier.MtoolsRoundTrip(image.Path);
        FatVerifier.Fsck(image.Path);
    }

    private static TempImage ExtractVolume(TempImage disk, long offset, long length)
    {
        var volume = new TempImage(0);
        using var source = disk.Open();
        using var target = new FileStream(volume.Path, FileMode.Create, FileAccess.Write);
        source.Position = offset;
        var buffer = new byte[1 << 20];
        var remaining = length;
        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            target.Write(buffer, 0, read);
            remaining -= read;
        }

        return volume;
    }
}
