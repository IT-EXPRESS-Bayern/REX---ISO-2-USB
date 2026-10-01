// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class NumericVersionTests
{
    private static readonly string[] Releases = ["24.04.3", "24.04.5.1", "24.04.5", "24.04.4"];

    [Theory]
    [InlineData("24.04.5.1", "24.04.5")]
    [InlineData("24.04.10", "24.04.9")]
    [InlineData("13.7.0", "13.6.0")]
    [InlineData("2026.2", "2026.1")]
    [InlineData("25.04", "24.10")]
    [InlineData("9", "8.99")]
    public void Newer_SortsAboveOlder(string newer, string older)
    {
        Assert.True(NumericVersion.Parse(newer) > NumericVersion.Parse(older));
        Assert.True(NumericVersion.Parse(older) < NumericVersion.Parse(newer));
        Assert.Equal(-1, Math.Sign(NumericVersion.Parse(older).CompareTo(NumericVersion.Parse(newer))));
    }

    [Fact]
    public void MissingTrailingSegments_CountAsZero()
    {
        Assert.Equal(NumericVersion.Parse("24.04"), NumericVersion.Parse("24.04.0"));
        Assert.Equal(NumericVersion.Parse("24.04").GetHashCode(), NumericVersion.Parse("24.04.0.0").GetHashCode());
        Assert.True(NumericVersion.Parse("24.04") < NumericVersion.Parse("24.04.0.1"));
    }

    [Fact]
    public void Text_KeepsTheSpellingOfTheDirectory()
    {
        var version = NumericVersion.Parse("24.04.5");

        Assert.Equal("24.04.5", version.ToString());
        Assert.Equal(24, version.Major);
        Assert.Equal(4, version.Minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("24.04-rc1")]
    [InlineData("v24")]
    [InlineData("1..2")]
    [InlineData("-1")]
    public void TryParse_RejectsAnythingButDottedNumbers(string text)
    {
        Assert.False(NumericVersion.TryParse(text, out _));
        Assert.Throws<FormatException>(() => NumericVersion.Parse(text));
    }

    [Fact]
    public void Max_FindsTheNewestOfMany()
    {
        var newest = Releases.Select(NumericVersion.Parse).Max();

        Assert.Equal("24.04.5.1", newest!.ToString());
    }
}
