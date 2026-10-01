// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Tests.Partitioning;

public class ChsGeometryTests
{
    [Theory]
    [InlineData(0, 0, 0, 1)]
    [InlineData(62, 0, 0, 63)]
    [InlineData(63, 0, 1, 1)]
    [InlineData(128, 0, 2, 3)]
    [InlineData(2048, 0, 32, 33)]
    [InlineData(16065, 1, 0, 1)]
    [InlineData(16065L * 1023, 1023, 0, 1)]
    public void FromLba_With255HeadsAnd63Sectors_FollowsTheClassicTranslation(long lba, int cylinder, int head, int sector)
    {
        var chs = ChsGeometry.Translated.FromLba(lba);

        Assert.Equal(new ChsAddress(cylinder, head, sector), chs);
    }

    [Fact]
    public void FromLba_BeyondTheThousandTwentyFourthCylinder_IsClampedToTheMaximum()
    {
        var geometry = ChsGeometry.Translated;

        Assert.Equal(ChsAddress.Unrepresentable, geometry.FromLba(geometry.AddressableSectors));
        Assert.Equal(ChsAddress.Unrepresentable, geometry.FromLba(long.MaxValue / 2));
        Assert.Equal(new ChsAddress(1023, 254, 62), geometry.FromLba(geometry.AddressableSectors - 2));

        // With 255 heads the very last addressable sector happens to coincide with the marker value.
        Assert.Equal(ChsAddress.Unrepresentable, geometry.FromLba(geometry.AddressableSectors - 1));
        Assert.Equal(new ChsAddress(1023, 254, 63), ChsAddress.Unrepresentable);
    }

    [Fact]
    public void ToLba_InvertsFromLba()
    {
        var geometry = new ChsGeometry(16, 63);
        foreach (var lba in new long[] { 0, 1, 62, 63, 1007, 1008, 100_000, 1_000_000 })
        {
            Assert.Equal(lba, geometry.ToLba(geometry.FromLba(lba)));
        }
    }

    [Fact]
    public void AddressableSectors_IsTheEightGibibyteBarrierForTheTranslatedGeometry()
    {
        Assert.Equal(1024L * 255 * 63, ChsGeometry.Translated.AddressableSectors);
        Assert.Equal(8_422_686_720, ChsGeometry.Translated.AddressableSectors * 512);
    }

    [Theory]
    [InlineData(1_000_000, 16)]
    [InlineData(1_032_192, 16)]
    [InlineData(1_032_193, 32)]
    [InlineData(2_064_384, 32)]
    [InlineData(4_128_768, 64)]
    [InlineData(8_257_536, 128)]
    [InlineData(8_257_537, 255)]
    [InlineData(2_000_000_000, 255)]
    public void ForCapacity_PicksTheHeadCountAnOldBiosWould(long sectors, int heads)
    {
        var geometry = ChsGeometry.ForCapacity(sectors);

        Assert.Equal(heads, geometry.Heads);
        Assert.Equal(63, geometry.SectorsPerTrack);
    }

    [Fact]
    public void Constructor_RejectsImpossibleGeometries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChsGeometry(0, 63));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChsGeometry(257, 63));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChsGeometry(255, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChsGeometry(255, 64));
    }

    [Fact]
    public void DefaultStruct_IsRejectedInsteadOfDividingByZero()
    {
        Assert.Throws<InvalidOperationException>(() => default(ChsGeometry).FromLba(5));
    }

    [Theory]
    [InlineData(0, 32, 33, new byte[] { 0x20, 0x21, 0x00 })]
    [InlineData(1023, 254, 63, new byte[] { 0xFE, 0xFF, 0xFF })]
    [InlineData(1023, 255, 63, new byte[] { 0xFF, 0xFF, 0xFF })]
    [InlineData(0, 0, 2, new byte[] { 0x00, 0x02, 0x00 })]
    [InlineData(300, 5, 17, new byte[] { 0x05, 0x51, 0x2C })]
    public void ChsAddress_PacksCylinderBitsIntoTheSectorByte(int cylinder, int head, int sector, byte[] expected)
    {
        var bytes = new byte[3];
        new ChsAddress(cylinder, head, sector).WriteTo(bytes);

        Assert.Equal(expected, bytes);
        Assert.Equal(new ChsAddress(cylinder, head, sector), ChsAddress.Read(bytes));
    }
}
