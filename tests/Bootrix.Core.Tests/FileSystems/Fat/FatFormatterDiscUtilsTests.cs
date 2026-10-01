// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;
using DiscFatType = DiscUtils.Fat.FatType;
using DiscFileSystem = DiscUtils.Fat.FatFileSystem;

namespace Bootrix.Core.Tests.FileSystems.Fat;

/// <summary>A second, managed opinion on the formatter output: DiscUtils reads the BPB and writes files into the volume.</summary>
public class FatFormatterDiscUtilsTests
{
    private const long Mib = 1024 * 1024;
    private const long Gib = 1024 * Mib;

    [Theory]
    [InlineData(12, 512, 1440 * 1024L)]
    [InlineData(16, 512, 64 * Mib)]
    [InlineData(32, 512, 512 * Mib)]
    [InlineData(32, 512, 33 * Gib)]
    [InlineData(32, 4096, 2 * Gib)]
    [InlineData(16, 4096, 128 * Mib)]
    public void Format_Output_IsMountedAndWritableByDiscUtils(int type, int bytesPerSector, long bytes)
    {
        var options = new FatFormatOptions
        {
            TotalBytes = bytes,
            BytesPerSector = bytesPerSector,
            Type = (FatType)type,
            Label = "discfat",
            OemName = "BOOTRIX",
            VolumeId = 0x12345678,
            HiddenSectors = 2048,
            SectorsPerTrack = 63,
            Heads = 255,
            AssumeZeroed = true,
        };

        var backing = new SparseMemoryStream(bytes);
        var result = FatFormatter.Format(backing, options);
        var payload = new byte[70_000];
        new Random(7).NextBytes(payload);

        using (var fs = new DiscFileSystem(backing))
        {
            Assert.Equal((DiscFatType)type, fs.FatVariant);
            Assert.Equal("DISCFAT", fs.VolumeLabel);
            Assert.Equal("BOOTRIX", fs.OemName.Trim());
            Assert.Equal(0x12345678u, (uint)fs.VolumeId);
            Assert.Equal(bytesPerSector, fs.SectorSize);
            Assert.Equal(result.Layout.SectorsPerCluster, fs.SectorsPerCluster);
            Assert.Equal(result.Layout.ReservedSectors, fs.ReservedSectorCount);
            Assert.Equal(result.Layout.FatCount, fs.FatCount);
            Assert.Equal(2048, (int)fs.HiddenSectors);
            Assert.Equal(63, fs.SectorsPerTrack);
            Assert.Equal(255, fs.Heads);

            using (var file = fs.OpenFile("DATA.BIN", FileMode.Create))
            {
                file.Write(payload);
            }

            fs.CreateDirectory("BOOT");
            using (var nested = fs.OpenFile("BOOT\\NOTE.TXT", FileMode.Create))
            {
                nested.Write("hello"u8);
            }
        }

        using var reopened = new DiscFileSystem(backing);
        using (var file = reopened.OpenFile("DATA.BIN", FileMode.Open))
        {
            var read = new byte[payload.Length];
            file.ReadExactly(read);
            Assert.Equal(payload, read);
        }

        Assert.True(reopened.FileExists("BOOT\\NOTE.TXT"));
        Assert.Equal(["DATA.BIN"], reopened.GetFiles("\\").Select(Path.GetFileName));
    }

    [Fact]
    public void Format_Fat32_FreeSpaceMatchesTheClusterCount()
    {
        const long bytes = 600 * Mib;
        var backing = new SparseMemoryStream(bytes);
        var result = FatFormatter.Format(backing, new FatFormatOptions { TotalBytes = bytes, Type = FatType.Fat32, AssumeZeroed = true });

        using var fs = new DiscFileSystem(backing);

        // One cluster belongs to the root directory.
        Assert.Equal((result.Layout.ClusterCount - 1) * result.Layout.ClusterBytes, fs.AvailableSpace);
    }
}
