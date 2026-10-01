// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Tests.Images.Apple;

public sealed partial class ApmReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-apm-" + Guid.NewGuid().ToString("N"));

    public ApmReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void TryRead_StandardMap_ReturnsAllEntries()
    {
        var image = new byte[2 * 1024 * 1024];
        AppleTestImages.WriteApm(image, 512,
            ("Apple", "Apple_partition_map", 1, 63),
            ("disk image", "Apple_HFS", 64, 2000),
            ("", "Apple_Free", 2064, 2032));

        var map = ApmReader.TryRead(new MemoryStream(image));

        Assert.NotNull(map);
        Assert.Equal(512, map.BlockSize);
        Assert.True(map.HasDriverDescriptor);
        Assert.Equal(image.Length / 512, map.DeviceBlocks);
        Assert.Equal(3, map.Partitions.Count);
        var hfs = map.Partitions[1];
        Assert.Equal("disk image", hfs.Name);
        Assert.Equal(ApmPartitionTypes.Hfs, hfs.Type);
        Assert.Equal(64 * 512, hfs.StartOffset);
        Assert.Equal(2000 * 512, hfs.Length);
        Assert.Equal(1, hfs.Index);
        Assert.Equal(0x33u, hfs.Status);
    }

    [Fact]
    public void TryRead_EntriesInBlocksOf2048_UseThatBlockSize()
    {
        var image = new byte[1024 * 1024];
        AppleTestImages.WriteApm(image, 2048,
            ("Apple", "Apple_partition_map", 1, 4),
            ("Gap0", "ISO9660_data", 16, 16),
            ("HFSPLUS_Hybrid", "Apple_HFS", 32, 10));

        var map = ApmReader.TryRead(new MemoryStream(image));

        Assert.NotNull(map);
        Assert.Equal(2048, map.BlockSize);
        Assert.Equal(32 * 2048, map.Partitions[2].StartOffset);
        Assert.Equal(10 * 2048, map.Partitions[2].Length);
    }

    [Fact]
    public void TryRead_DescriptorSays2048ButEntriesAre512Apart_FollowsTheEntries()
    {
        var image = new byte[1024 * 1024];
        AppleTestImages.WriteApm(image, 512, ("Apple", "Apple_partition_map", 1, 63), ("x", "Apple_HFS", 64, 100));
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2), 2048);

        var map = ApmReader.TryRead(new MemoryStream(image));

        Assert.NotNull(map);
        Assert.Equal(512, map.BlockSize);
        Assert.Equal(2, map.Partitions.Count);
    }

    [Fact]
    public void TryRead_WithoutDriverDescriptor_StillFindsTheMap()
    {
        var image = new byte[64 * 1024];
        AppleTestImages.WriteApm(image, 512, ("x", "Apple_HFS", 8, 40));
        image.AsSpan(0, 16).Clear();

        var map = ApmReader.TryRead(new MemoryStream(image));

        Assert.NotNull(map);
        Assert.False(map.HasDriverDescriptor);
        Assert.Single(map.Partitions);
    }

    [Fact]
    public void TryRead_DescriptorWithoutEntries_IsNotAMap()
    {
        var image = new byte[64 * 1024];
        BinaryPrimitives.WriteUInt16BigEndian(image, 0x4552);
        BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(2), 512);

        Assert.Null(ApmReader.TryRead(new MemoryStream(image)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(600)]
    [InlineData(100_000)]
    public void TryRead_RandomOrShortData_ReturnsNull(int length)
    {
        Assert.Null(ApmReader.TryRead(new MemoryStream(ImageTestData.Random(length, 8))));
    }

    [Fact]
    public void TryRead_EntryCountOfZeroOrAbsurd_ReturnsNull()
    {
        foreach (var count in new uint[] { 0, 1000, uint.MaxValue })
        {
            var image = new byte[64 * 1024];
            AppleTestImages.WriteApm(image, 512, ("x", "Apple_HFS", 8, 40));
            BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(512 + 4), count);

            Assert.Null(ApmReader.TryRead(new MemoryStream(image)));
        }
    }

    [Fact]
    public void TryRead_CountLargerThanTheEntriesPresent_StopsAtTheFirstGap()
    {
        var image = new byte[64 * 1024];
        AppleTestImages.WriteApm(image, 512, ("a", "Apple_HFS", 8, 40), ("b", "Apple_HFS", 48, 40));
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(512 + 4), 20);

        var map = ApmReader.TryRead(new MemoryStream(image));

        Assert.NotNull(map);
        Assert.Equal(2, map.Partitions.Count);
    }

    [Fact]
    public void TryRead_ImageEndingInsideTheMap_ReturnsWhatIsThere()
    {
        var full = new byte[64 * 1024];
        AppleTestImages.WriteApm(full, 512, ("a", "Apple_HFS", 8, 40), ("b", "Apple_HFS", 48, 40), ("c", "Apple_HFS", 90, 40));

        var map = ApmReader.TryRead(new MemoryStream(full, 0, 512 + 0x88 + 100));

        Assert.NotNull(map);
        Assert.Single(map.Partitions);
    }

    [Theory]
    [InlineData("Apple_HFS", true, false)]
    [InlineData("apple_hfsx", true, false)]
    [InlineData("Apple_partition_map", true, true)]
    [InlineData("Apple_Driver43", true, true)]
    [InlineData("Apple_Free", true, true)]
    [InlineData("MS-DOS", false, false)]
    [InlineData("Windows_FAT_32", false, false)]
    public void PartitionTypes_AreClassified(string type, bool apple, bool structural)
    {
        Assert.Equal(apple, ApmPartitionTypes.IsApple(type));
        Assert.Equal(structural, ApmPartitionTypes.IsStructural(type));
    }

    [RequiresToolFact("parted")]
    public void TryRead_ImageCreatedByParted_MatchesThePartedListing()
    {
        var path = Path.Combine(_directory, "apm.img");
        File.WriteAllBytes(path, new byte[24 * 1024 * 1024]);
        var (code, output) = ExternalTool.Run(ExternalTool.Find("parted")!,
            ["-s", path, "mklabel", "mac", "mkpart", "primary", "hfs+", "1MiB", "9MiB", "mkpart", "primary", "hfs", "9MiB", "20MiB"]);
        Assert.True(code == 0, output);
        var (_, listing) = ExternalTool.Run(ExternalTool.Find("parted")!, ["-s", path, "unit", "s", "print"]);

        using var stream = File.OpenRead(path);
        var map = ApmReader.TryRead(stream);

        Assert.NotNull(map);
        foreach (Match row in PartedRow().Matches(listing))
        {
            var number = int.Parse(row.Groups["n"].Value, CultureInfo.InvariantCulture);
            var start = long.Parse(row.Groups["start"].Value, CultureInfo.InvariantCulture);
            var size = long.Parse(row.Groups["size"].Value, CultureInfo.InvariantCulture);
            var entry = map.Partitions.Single(p => p.Index == number - 1);
            Assert.Equal(start * 512, entry.StartOffset);
            Assert.Equal(size * 512, entry.Length);
        }

        Assert.Equal(ApmPartitionTypes.PartitionMap, map.Partitions[0].Type);
        Assert.Equal(2, map.Partitions.Count(p => p.Type == ApmPartitionTypes.Hfs));
        Assert.Contains(map.Partitions, p => p.Type == ApmPartitionTypes.Free);
    }

    [RequiresToolFact("xorriso")]
    public void TryRead_HybridIsoFromXorriso_FindsTheMapWith2048ByteBlocks()
    {
        var tree = Path.Combine(_directory, "tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "hello");
        var iso = Path.Combine(_directory, "hybrid.iso");
        var (code, output) = ExternalTool.Run(ExternalTool.Find("xorriso")!,
            ["-as", "mkisofs", "-r", "-hfsplus", "-apm-block-size", "2048", "-o", iso, tree]);
        Assert.True(code == 0, output);

        using var stream = File.OpenRead(iso);
        var map = ApmReader.TryRead(stream);

        Assert.NotNull(map);
        Assert.Equal(2048, map.BlockSize);
        Assert.Contains(map.Partitions, p => p.Type == ApmPartitionTypes.PartitionMap);
        Assert.Contains(map.Partitions, p => p.Type == ApmPartitionTypes.Hfs);
        Assert.All(map.Partitions, p => Assert.True(p.StartOffset + p.Length <= stream.Length));
    }

    [GeneratedRegex(@"^\s*(?<n>\d+)\s+(?<start>\d+)s\s+(?<end>\d+)s\s+(?<size>\d+)s", RegexOptions.Multiline)]
    private static partial Regex PartedRow();
}
