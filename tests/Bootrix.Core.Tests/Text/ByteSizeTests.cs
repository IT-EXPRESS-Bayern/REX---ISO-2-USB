// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Text;

namespace Bootrix.Core.Tests.Text;

public class ByteSizeTests
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1536L, "2 KB")]
    [InlineData(5L << 20, "5 MB")]
    [InlineData(15_500_000_000L, "14,44 GB")]
    [InlineData(1L << 40, "1 TB")]
    public void Format_UsesBinaryUnitsAndTheCultureSeparator(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes, De));
    }

    [Fact]
    public void Format_UsesDotForEnglish()
    {
        Assert.Equal("14.44 GB", ByteSize.Format(15_500_000_000L, En));
    }

    [Fact]
    public void Format_KeepsTheSignOfNegativeValues()
    {
        Assert.Equal("-2 MB", ByteSize.Format(-(2L << 20), En));
    }

    [Theory]
    [InlineData(0.0, "")]
    [InlineData(0.5, "")]
    [InlineData(31_457_280.0, "30 MB/s")]
    public void FormatRate_IsEmptyUntilThereIsASpeed(double rate, string expected)
    {
        Assert.Equal(expected, ByteSize.FormatRate(rate, En));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(-5, "")]
    [InlineData(59, "0:59")]
    [InlineData(252, "4:12")]
    [InlineData(4985, "1:23:05")]
    public void FormatDuration_ShowsHoursOnlyWhenNeeded(int? seconds, string expected)
    {
        TimeSpan? duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null;

        Assert.Equal(expected, ByteSize.FormatDuration(duration));
    }
}
