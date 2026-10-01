// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical;

namespace Bootrix.Core.Tests.Optical;

public class MsfTests
{
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(74, "00:00:74")]
    [InlineData(75, "00:01:00")]
    [InlineData(4499, "00:59:74")]
    [InlineData(4500, "01:00:00")]
    [InlineData(333_000, "74:00:00")]
    public void FramesRoundTrip(int frames, string text)
    {
        var msf = Msf.FromFrames(frames);

        Assert.Equal(text, msf.ToString());
        Assert.Equal(frames, msf.ToFrames());
        Assert.Equal(msf, Msf.Parse(text));
    }

    [Theory]
    [InlineData(-150, "00:00:00")]
    [InlineData(0, "00:02:00")]
    [InlineData(16, "00:02:16")]
    [InlineData(333_000 - 150, "74:00:00")]
    public void LbaIsOffsetByTheTwoSecondLeadIn(int lba, string expected)
    {
        var msf = Msf.FromLba(lba);

        Assert.Equal(expected, msf.ToString());
        Assert.Equal(lba, msf.ToLba());
    }

    [Theory]
    [InlineData("00:00:00", true)]
    [InlineData("99:59:74", true)]
    [InlineData("120:00:00", true)]
    [InlineData("00:60:00", false)]
    [InlineData("00:00:75", false)]
    [InlineData("0:0", false)]
    [InlineData("a:b:c", false)]
    [InlineData("-1:00:00", false)]
    [InlineData("", false)]
    public void ParsingRejectsImpossibleTimes(string text, bool valid)
    {
        Assert.Equal(valid, Msf.TryParse(text, out _));
    }

    [Fact]
    public void ParseThrowsOnGarbage()
    {
        Assert.Throws<FormatException>(() => Msf.Parse("nonsense"));
    }

    [Fact]
    public void TimesCompareByPosition()
    {
        Assert.True(new Msf(0, 1, 74) < new Msf(0, 2, 0));
        Assert.True(new Msf(1, 0, 0) > new Msf(0, 59, 74));
        Assert.Equal(0, new Msf(0, 2, 0).CompareTo(Msf.FromFrames(150)));
    }
}
