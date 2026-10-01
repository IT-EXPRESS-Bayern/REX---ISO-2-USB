// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Tests.Partitioning;

public class PartitionAlignmentTests
{
    [Theory]
    [InlineData(0, 1024, 0)]
    [InlineData(1, 1024, 1024)]
    [InlineData(1024, 1024, 1024)]
    [InlineData(1025, 1024, 2048)]
    public void AlignUp_RoundsToTheNextMultiple(long value, long alignment, long expected)
    {
        Assert.Equal(expected, PartitionAlignment.AlignUp(value, alignment));
    }

    [Theory]
    [InlineData(0, 1024, 0)]
    [InlineData(1023, 1024, 0)]
    [InlineData(1024, 1024, 1024)]
    [InlineData(3000, 1024, 2048)]
    public void AlignDown_RoundsToThePreviousMultiple(long value, long alignment, long expected)
    {
        Assert.Equal(expected, PartitionAlignment.AlignDown(value, alignment));
    }

    [Theory]
    [InlineData(512, 2048)]
    [InlineData(4096, 256)]
    public void OneMebibyteLba_DependsOnTheSectorSize(int sectorSize, long expected)
    {
        Assert.Equal(expected, PartitionAlignment.OneMebibyteLba(sectorSize));
    }

    [Theory]
    [InlineData(LegacyPartitionStart.Lba63, 512, 63)]
    [InlineData(LegacyPartitionStart.Kib64, 512, 128)]
    [InlineData(LegacyPartitionStart.Lba63, 4096, 8)]
    [InlineData(LegacyPartitionStart.Kib64, 4096, 16)]
    public void LegacyStartLba_KeepsTheByteOffsetOfTheClassicLayouts(LegacyPartitionStart start, int sectorSize, long expected)
    {
        Assert.Equal(expected, PartitionAlignment.LegacyStartLba(start, sectorSize));
    }

    [Fact]
    public void LegacyStart_OnFourKibSectors_IsAlwaysFourKibAligned()
    {
        foreach (var start in Enum.GetValues<LegacyPartitionStart>())
        {
            Assert.True(PartitionAlignment.IsAligned(PartitionAlignment.LegacyStartLba(start, 4096), 4096));
        }
    }

    [Fact]
    public void IsAligned_FlagsTheClassicSixtyThreeSectorOffset()
    {
        Assert.False(PartitionAlignment.IsAligned(63, 512));
        Assert.True(PartitionAlignment.IsAligned(64, 512));
        Assert.True(PartitionAlignment.IsAligned(2048, 512));
        Assert.True(PartitionAlignment.IsAligned(128, 512, 65_536));
        Assert.False(PartitionAlignment.IsAligned(8, 512, 65_536));
    }

    [Theory]
    [InlineData(512, 2048)]
    [InlineData(4096, 256)]
    public void BootloaderGap_IsOneMebibyte(int sectorSize, long expected)
    {
        Assert.Equal(expected, PartitionAlignment.BootloaderGapLba(sectorSize));
        Assert.Equal(1024 * 1024, PartitionAlignment.BootloaderGapBytes);
    }
}
