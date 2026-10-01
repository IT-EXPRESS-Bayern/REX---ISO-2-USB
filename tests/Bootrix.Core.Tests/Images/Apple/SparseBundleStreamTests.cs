// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Tests.Images.Apple;

public sealed class SparseBundleStreamTests : IDisposable
{
    private const int BandBytes = 4096;

    private readonly string _bundle = Path.Combine(Path.GetTempPath(), "bootrix-bundle-" + Guid.NewGuid().ToString("N") + ".sparsebundle");

    public void Dispose()
    {
        if (Directory.Exists(_bundle))
        {
            Directory.Delete(_bundle, recursive: true);
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_BundleWithMissingBands_ReturnsTheVolume(bool integerValues)
    {
        var volume = SparseImageBuilder.SparseVolume(20, BandBytes, 1000, 1);
        SparseImageBuilder.BuildBundle(_bundle, volume, BandBytes, integerValues);

        using var stream = SparseBundleStream.Open(_bundle);

        Assert.Equal(volume.Length, stream.Length);
        Assert.Equal(BandBytes, stream.BandSize);
        Assert.Equal(volume, ReadAll(stream));
    }

    [Fact]
    public void Read_BandNumbersAboveFifteen_UseLowercaseHexadecimalFileNames()
    {
        var volume = SparseImageBuilder.SparseVolume(40, BandBytes, BandBytes, 2);
        SparseImageBuilder.BuildBundle(_bundle, volume, BandBytes);

        Assert.True(File.Exists(Path.Combine(_bundle, "bands", "10")));
        Assert.True(File.Exists(Path.Combine(_bundle, "bands", "1c")));
        using var stream = SparseBundleStream.Open(_bundle);
        Assert.Equal(volume, ReadAll(stream));
    }

    [Fact]
    public void Read_BandFileShorterThanTheBand_PadsWithZeros()
    {
        var volume = SparseImageBuilder.SparseVolume(4, BandBytes, BandBytes, 3);
        SparseImageBuilder.BuildBundle(_bundle, volume, BandBytes);
        File.WriteAllBytes(Path.Combine(_bundle, "bands", "0"), volume.AsSpan(0, 1000).ToArray());
        var expected = (byte[])volume.Clone();
        expected.AsSpan(1000, BandBytes - 1000).Clear();

        using var stream = SparseBundleStream.Open(_bundle);

        Assert.Equal(expected, ReadAll(stream));
    }

    [Fact]
    public void Read_RandomRanges_MatchTheVolume()
    {
        var volume = SparseImageBuilder.SparseVolume(30, BandBytes, 2500, 4);
        SparseImageBuilder.BuildBundle(_bundle, volume, BandBytes);
        using var stream = SparseBundleStream.Open(_bundle);
        var random = new Random(6);
        var buffer = new byte[30_000];

        for (var i = 0; i < 200; i++)
        {
            var offset = random.Next(0, volume.Length);
            var length = random.Next(1, buffer.Length);
            stream.Position = offset;

            var read = stream.Read(buffer, 0, length);

            Assert.Equal(Math.Min(length, volume.Length - offset), read);
            Assert.True(volume.AsSpan(offset, read).SequenceEqual(buffer.AsSpan(0, read)));
        }
    }

    [Fact]
    public void Open_WithoutBandsDirectory_ReadsAsZeros()
    {
        Directory.CreateDirectory(_bundle);
        File.WriteAllText(Path.Combine(_bundle, "Info.plist"),
            "<plist version=\"1.0\"><dict><key>size</key><integer>10000</integer><key>band-size</key><integer>4096</integer></dict></plist>");

        using var stream = SparseBundleStream.Open(_bundle);

        Assert.Equal(10000, stream.Length);
        Assert.All(ReadAll(stream), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Open_WithoutInfoPlist_IsReportedAsUnreadable()
    {
        Directory.CreateDirectory(_bundle);

        Assert.Equal(ErrorCode.ImageUnreadable, Assert.Throws<BootrixException>(() => SparseBundleStream.Open(_bundle)).Code);
    }

    [Theory]
    [InlineData("<plist version=\"1.0\"><dict><key>band-size</key><integer>4096</integer></dict></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>size</key><integer>4096</integer></dict></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>size</key><integer>0</integer><key>band-size</key><integer>4096</integer></dict></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>size</key><integer>-5</integer><key>band-size</key><integer>4096</integer></dict></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>size</key><integer>4096</integer><key>band-size</key><integer>0</integer></dict></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>size</key><integer>4096</integer><key>band-size</key><integer>99999999999</integer></dict></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>size</key><integer>999999999999999999</integer><key>band-size</key><integer>4096</integer></dict></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><key>size</key><string>abc</string><key>band-size</key><integer>4096</integer></dict></plist>")]
    [InlineData("not a plist at all")]
    public void Open_InvalidInfoPlist_IsCorruption(string plist)
    {
        Directory.CreateDirectory(_bundle);
        File.WriteAllText(Path.Combine(_bundle, "Info.plist"), plist);

        Assert.Equal(ErrorCode.ImageCorrupt, Assert.Throws<BootrixException>(() => SparseBundleStream.Open(_bundle)).Code);
    }

    [Fact]
    public void Open_OtherBundleType_IsUnsupported()
    {
        Directory.CreateDirectory(_bundle);
        File.WriteAllText(Path.Combine(_bundle, "Info.plist"),
            "<plist version=\"1.0\"><dict><key>diskimage-bundle-type</key><string>com.apple.diskimage.sparsebundle.v2</string>"
            + "<key>size</key><integer>10000</integer><key>band-size</key><integer>4096</integer></dict></plist>");

        Assert.Equal(ErrorCode.ImageUnsupported, Assert.Throws<BootrixException>(() => SparseBundleStream.Open(_bundle)).Code);
    }

    [Fact]
    public void Dispose_ReleasesTheBandFile()
    {
        var volume = SparseImageBuilder.SparseVolume(3, BandBytes, BandBytes, 5);
        SparseImageBuilder.BuildBundle(_bundle, volume, BandBytes);
        var stream = SparseBundleStream.Open(_bundle);
        stream.ReadExactly(new byte[100]);

        stream.Dispose();

        File.Delete(Path.Combine(_bundle, "bands", "0"));
        Assert.False(File.Exists(Path.Combine(_bundle, "bands", "0")));
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }
}
