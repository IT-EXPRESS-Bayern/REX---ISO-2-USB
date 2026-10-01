// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Library;

namespace Bootrix.Core.Tests.Library;

public class VersionOrderTests
{
    private static int Compare(string? older, string? newer) => Math.Sign(VersionOrder.Instance.Compare(older, newer));

    [Theory]
    [InlineData("1.0", "1.0.1")]
    [InlineData("9", "10")]
    [InlineData("9.0", "10.0")]
    [InlineData("2.06", "2.06s4")]
    [InlineData("2.04s1", "2.06s4")]
    [InlineData("24.04.3", "24.04.10")]
    [InlineData("24.04", "24.04.1")]
    [InlineData("23H2", "24H2")]
    [InlineData("24H1", "24H2")]
    [InlineData("13.01", "13.02")]
    [InlineData("3.3.3-9", "3.3.3-37")]
    [InlineData("1.8.1-6", "1.8.2-1")]
    [InlineData("1.0-rc1", "1.0")]
    [InlineData("1.0-beta2", "1.0-rc1")]
    [InlineData("1.0", "1.0.0.1")]
    [InlineData("8.00", "8.10")]
    [InlineData("99999999999999999999998", "99999999999999999999999")]
    [InlineData("abc", "1")]
    [InlineData("1.0a", "1.0b")]
    [InlineData(null, "1")]
    [InlineData("", "1")]
    public void OrdersOlderBeforeNewer(string? older, string? newer)
    {
        Assert.Equal(-1, Compare(older, newer));
        Assert.Equal(1, Compare(newer, older));
    }

    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("1.0", "1,0")]
    [InlineData("1.0", "1-0")]
    [InlineData("01.00", "1.0")]
    [InlineData("1.0A", "1.0a")]
    [InlineData(null, null)]
    public void TreatsSeparatorsLeadingZerosAndCaseAsEqual(string? a, string? b)
    {
        Assert.Equal(0, Compare(a, b));
    }

    [Fact]
    public void SortsARealisticListTheWayPeopleExpect()
    {
        string[] shuffled = ["13.02", "2.06s4", "9.0", "13.01", "10.1", "1.8.1-6", "2.06s3", "2.06"];

        var sorted = shuffled.Order(VersionOrder.Instance).ToArray();

        Assert.Equal(["1.8.1-6", "2.06", "2.06s3", "2.06s4", "9.0", "10.1", "13.01", "13.02"], sorted);
    }
}
