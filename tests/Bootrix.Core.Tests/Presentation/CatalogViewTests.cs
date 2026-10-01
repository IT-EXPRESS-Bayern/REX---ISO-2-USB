// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Catalog;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Presentation;

public class CatalogViewTests
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en-US") };
    private static readonly FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private static CatalogVariant Variant(Func<CatalogVariant, CatalogVariant>? tweak = null)
    {
        var variant = new CatalogVariant { Id = "v", ProductId = "p", Provider = "x", Name = "Windows 11 25H2" };
        return tweak?.Invoke(variant) ?? variant;
    }

    [Fact]
    public void DetailsListLanguageArchitectureAndSize()
    {
        var description = CatalogView.Describe(Variant(v => v with { Language = "de-DE", Architectures = ["x64", "arm64"], SizeBytes = 7L << 30 }), English, Time);

        Assert.Equal("Windows 11 25H2", description.Title);
        Assert.Equal("de-DE  ·  x64, arm64  ·  7 GB", description.Details);
        Assert.False(description.IsManual);
    }

    [Fact]
    public void RecommendedAndManualAreMarked()
    {
        var description = CatalogView.Describe(Variant(v => v with { IsRecommended = true, ManualUrl = "https://example.org" }), English, Time);

        Assert.Contains("recommended", description.Details, StringComparison.Ordinal);
        Assert.Contains("manual download", description.Details, StringComparison.Ordinal);
        Assert.True(description.IsManual);
    }

    [Theory]
    [InlineData(2025, 10, 14, true, "support ended")]
    [InlineData(2027, 5, 1, false, "support until")]
    public void SupportEndDecidesBetweenEndedAndUpcoming(int year, int month, int day, bool ended, string expected)
    {
        var description = CatalogView.Describe(Variant(v => v with { EndOfSupport = new DateOnly(year, month, day) }), English, Time);

        Assert.Equal(ended, description.SupportEnded);
        Assert.Contains(expected, description.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.org/dir/ubuntu-24.04.iso?token=1", "ubuntu-24.04.iso")]
    [InlineData("https://example.org/dir/My%20Image.iso", "My Image.iso")]
    [InlineData("https://example.org/", "download.bin")]
    [InlineData("https://example.org/a/%2e%2e%2f%2e%2e%2fevil.iso", "evil.iso")]
    public void FileNameComesFromTheAddressAndNeverFromAPath(string url, string expected)
    {
        Assert.Equal(expected, CatalogView.FileNameFor(new Uri(url)));
    }

    [Fact]
    public void EveryFamilyHasAName()
    {
        foreach (var family in Enum.GetValues<CatalogFamily>())
        {
            Assert.True(English.Has("Cat.Family." + family), family.ToString());
        }
    }
}
