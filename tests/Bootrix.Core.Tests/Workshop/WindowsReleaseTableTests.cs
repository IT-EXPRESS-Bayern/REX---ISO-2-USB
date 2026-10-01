// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Advice;

namespace Bootrix.Core.Tests.Workshop;

public class WindowsReleaseTableTests
{
    [Fact]
    public void Releases_AreConsistent()
    {
        foreach (var release in WindowsReleaseTable.Releases)
        {
            Assert.True(release.EndOfServicing > release.GeneralAvailability, release.Version);
            Assert.Matches("^[0-9]{2}H[12]$", release.Version);
        }

        Assert.Equal(WindowsReleaseTable.Releases.Count, WindowsReleaseTable.Releases.Select(r => (r.Product, r.Version)).Distinct().Count());
    }

    [Theory]
    [InlineData("2026-10-01", "25H2")]
    [InlineData("2026-10-28", "25H2")]
    [InlineData("2026-10-29", "26H2")]
    [InlineData("2025-11-01", "25H2")]
    [InlineData("2025-09-30", "24H2")]
    [InlineData("2024-12-01", "24H2")]
    public void Recommended_IsTheNewestReleaseThatHasSettled(string today, string expected)
    {
        var release = WindowsReleaseTable.Recommended(WindowsProduct.Windows11, DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expected, release!.Version);
    }

    [Fact]
    public void Recommended_NeverPicksTheReleaseForNewDevicesOnly()
    {
        var release = WindowsReleaseTable.Recommended(WindowsProduct.Windows11, new DateOnly(2026, 3, 1));

        Assert.False(release!.NewDevicesOnly);
        Assert.NotEqual("26H1", release.Version);
    }

    [Fact]
    public void ForNewDevices_ReturnsTheScopedRelease_OnceItIsAvailable()
    {
        Assert.Equal("26H1", WindowsReleaseTable.ForNewDevices(WindowsProduct.Windows11, new DateOnly(2026, 10, 1))!.Version);
        Assert.Null(WindowsReleaseTable.ForNewDevices(WindowsProduct.Windows11, new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Recommended_Windows10_IsTheLastReleaseEvenAfterItsEnd()
    {
        var release = WindowsReleaseTable.Recommended(WindowsProduct.Windows10, new DateOnly(2026, 10, 1));

        Assert.Equal("22H2", release!.Version);
        Assert.True(release.EndOfServicing < new DateOnly(2026, 10, 1));
    }

    [Fact]
    public void Recommended_BeforeAnyReleaseExisted_FallsBackToTheNewestOfTheProduct()
    {
        Assert.NotNull(WindowsReleaseTable.Recommended(WindowsProduct.Windows11, new DateOnly(2020, 1, 1)));
    }

    [Theory]
    [InlineData("24H2", "2026-10-13")]
    [InlineData("25H2", "2027-10-12")]
    [InlineData("26H1", "2028-03-15")]
    [InlineData("26H2", "2028-10-10")]
    public void EndOfServicing_MatchesTheLifecyclePageOfHomeAndPro(string version, string expected)
    {
        var release = WindowsReleaseTable.Find(WindowsProduct.Windows11, version);

        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), release!.EndOfServicing);
    }

    [Fact]
    public void Find_IsCaseInsensitiveAndNullSafe()
    {
        Assert.NotNull(WindowsReleaseTable.Find(WindowsProduct.Windows11, "25h2"));
        Assert.Null(WindowsReleaseTable.Find(WindowsProduct.Windows11, null));
        Assert.Null(WindowsReleaseTable.Find(WindowsProduct.Windows10, "25H2"));
    }

    [Fact]
    public void Names_FitTheCatalogQuery()
    {
        var release = WindowsReleaseTable.Find(WindowsProduct.Windows11, "25H2")!;

        Assert.Equal("windows11", release.CatalogName);
        Assert.Equal("Windows 11", release.ProductName);
        Assert.Equal("windows10", WindowsReleaseTable.Find(WindowsProduct.Windows10, "22H2")!.CatalogName);
    }
}
