// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Rescue;
using Bootrix.Core.Localization;

namespace Bootrix.Core.Tests.Catalog.Rescue;

/// <summary>
/// The rules for the data that ships with Bootrix. The reader already refuses malformed documents; these tests
/// add what only a person maintaining the file can get wrong: forgotten translations, unverified links, missing notices.
/// </summary>
public partial class EmbeddedRescueCatalogTests
{
    private static readonly RescueCatalogDocument Catalog = EmbeddedRescueCatalog.Load();

    private static readonly string[] KnownLicenses =
    [
        "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later", "Apache-2.0", "proprietary", "proprietary-freeware",
    ];

    private static readonly string[] PasswordTools =
    [
        "chntpw", "winpass", "NT Password Edit", "Windows Login Unlocker", "Lazesoft Password Recovery", "ophcrack",
    ];

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    public static TheoryData<string> Entries => [.. Catalog.Entries.Select(e => e.Id)];

    private static RescueEntry Entry(string id) => Catalog.Entries.Single(e => e.Id == id);

    [Fact]
    public void CatalogParsesAndCarriesAVersionAndValidity()
    {
        Assert.True(Catalog.Version >= 1);
        Assert.True(Catalog.Expires > Catalog.Issued);
        Assert.True(Catalog.Expires <= Catalog.Issued.AddYears(1), "a catalog that is valid for more than a year hides stale links");
    }

    [Fact]
    public void ContainsTheToolsTheProductNeeds()
    {
        string[] expected =
        [
            "systemrescue", "gparted-live", "clonezilla", "rescuezilla", "hirens-bootcd-pe", "medicat-usb", "kaspersky-rescue-disk",
            "drweb-livedisk", "ubcd", "chntpw", "ophcrack", "trinity-rescue-kit", "boot-repair-disk", "super-grub2-disk",
            "memtest86plus", "memtest86", "seatools", "windows-recovery-drive", "windows-adk-winpe",
        ];

        Assert.All(expected, id => Assert.Contains(Catalog.Entries, e => e.Id == id));
    }

    [Fact]
    public void EveryCategoryIsRepresented()
    {
        var used = Catalog.Entries.Select(e => e.Category).ToHashSet();

        Assert.All(Enum.GetValues<RescueCategory>(), c => Assert.Contains(c, used));
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void EntryHasDistinctGermanAndEnglishTexts(string id)
    {
        var entry = Entry(id);

        Assert.False(string.IsNullOrWhiteSpace(entry.Name));
        Assert.False(string.IsNullOrWhiteSpace(entry.Description.De));
        Assert.False(string.IsNullOrWhiteSpace(entry.Description.En));
        Assert.NotEqual(entry.Description.De, entry.Description.En);
        Assert.True(entry.Description.De.Length < 400 && entry.Description.En.Length < 400, "descriptions are meant to be short");
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void EntryUsesAKnownLicenseAndAnHttpsHomepage(string id)
    {
        var entry = Entry(id);

        var parts = entry.License.Split([" AND ", " OR "], StringSplitOptions.None);
        Assert.All(parts, part => Assert.Contains(part, KnownLicenses));
        Assert.Equal(Uri.UriSchemeHttps, entry.Homepage.Scheme);
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void EveryNoticeAndHintHasAGermanAndAnEnglishText(string id)
    {
        var german = new Localizer { Culture = CultureInfo.GetCultureInfo("de-DE") };
        var english = new Localizer { Culture = CultureInfo.GetCultureInfo("en-US") };

        foreach (var key in Entry(id).Notices.Concat(Entry(id).Hints))
        {
            Assert.True(german.Has(key) && english.Has(key), key);
            Assert.NotEqual(german.Get(key), english.Get(key));
            Assert.NotEqual(key, german.Get(key));
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void DownloadableVariantsAreCheckedAndAddressedOverHttps(string id)
    {
        foreach (var variant in Entry(id).Variants.Where(v => v.Sources.Count > 0))
        {
            Assert.NotNull(variant.VerifiedOn);
            Assert.True(variant.VerifiedOn <= Catalog.Issued, $"{id}/{variant.Id} was verified after the catalog was issued");
            Assert.False(string.IsNullOrEmpty(variant.FileName), $"{id}/{variant.Id} has no file name");
            Assert.All(variant.Sources, s =>
            {
                Assert.Equal(Uri.UriSchemeHttps, s.Url.Scheme);
                Assert.Contains('.', s.Url.Host);
            });

            if (variant.Sha256 is { } hash)
            {
                Assert.Matches(Sha256Pattern(), hash);
                Assert.NotNull(variant.Size);
                Assert.NotNull(variant.HashOrigin);
            }
            else
            {
                Assert.Single(variant.Sources);
                Assert.Contains("Hint.Rescue.UpdatedInPlace", Entry(id).Hints);
            }

            if (variant.HashOrigin == RescueHashOrigin.Pinned)
            {
                Assert.NotNull(variant.Sha256);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void ManualVariantsOnlyRecommendWhatCanBeKnownWithoutTheFile(string id)
    {
        foreach (var variant in Entry(id).Variants.Where(v => v.ManualUrl is not null))
        {
            Assert.Contains(variant.WriteMode, new[] { RescueWriteMode.None, RescueWriteMode.Auto });
            Assert.Empty(variant.Sources);
        }
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void FirmwareSupportIsConsistent(string id)
    {
        foreach (var variant in Entry(id).Variants.Where(v => v.Sources.Count > 0))
        {
            Assert.False(variant.Firmware is { Bios: BootSupport.No, Uefi: BootSupport.No }, $"{id}/{variant.Id} claims to boot neither way");
            Assert.False(variant.Firmware is { SecureBoot: BootSupport.Yes, Uefi: BootSupport.No }, $"{id}/{variant.Id} claims Secure Boot without UEFI");
        }
    }

    [Fact]
    public void EveryPasswordToolCarriesTheAuthorizedUseNotice()
    {
        foreach (var entry in Catalog.Entries)
        {
            var isPasswordTool =
                entry.Category == RescueCategory.OfflinePasswordReset
                || entry.Description.En.Contains("password", StringComparison.OrdinalIgnoreCase)
                || entry.IncludedTools.Any(t => PasswordTools.Contains(t, StringComparer.OrdinalIgnoreCase))
                || PasswordTools.Any(t => entry.Name.Contains(t, StringComparison.OrdinalIgnoreCase));

            if (isPasswordTool)
            {
                Assert.Contains(RescueNotices.AuthorizedUseOnly, entry.Notices);
            }
        }
    }

    [Fact]
    public void ThePasswordResetCategoryIsNotEmpty()
    {
        var tools = Catalog.Entries.Where(e => e.Category == RescueCategory.OfflinePasswordReset).Select(e => e.Id).ToList();

        Assert.Contains("chntpw", tools);
        Assert.Contains("ophcrack", tools);
    }

    [Fact]
    public void WinPeIsRecommendedAsWindowsPeOnlyWhereTheImageIsOne()
    {
        Assert.All(
            Catalog.Entries.SelectMany(e => e.Variants.Select(v => (Entry: e, Variant: v))).Where(x => x.Variant.WriteMode == RescueWriteMode.WindowsPe),
            x => Assert.Equal(RescueCategory.WinPeRepair, x.Entry.Category));
    }
}
