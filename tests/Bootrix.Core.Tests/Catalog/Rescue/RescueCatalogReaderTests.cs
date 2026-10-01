// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bootrix.Core.Catalog.Rescue;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Catalog.Rescue;

public class RescueCatalogReaderTests
{
    private const string Hash = "3a4c9877b483ab46d7c3fbe165a0db275e1ae3cfe56a5657e5a47c2f99a99d1e";

    /// <summary>One change to <see cref="RescueFixtures.Minimal"/> and the part of the message that has to name the problem. Single quotes stand for double quotes.</summary>
    public static TheoryData<string, string, string> Rejections => new()
    {
        { "'schemaVersion': 1", "'schemaVersion': 2", "schema version 2" },
        { "'schemaVersion': 1,", "", "schema version (none)" },
        { "'version': 3", "'version': 0", "version must be a positive number" },
        { "'expires': '2027-04-01'", "'expires': '2026-10-01'", "expires after issued" },
        { "'issued': '2026-10-01'", "'issued': 'yesterday'", "could not be converted" },
        { "'id': 'sample'", "'id': 'Sample'", "lower case letters" },
        { "'id': 'sample'", "'id': ''", "lower case letters" },
        { "'name': 'Sample'", "'name': ' '", "name is missing" },
        { "'description': { 'de': 'Beispiel', 'en': 'Example' }", "'description': { 'de': 'Beispiel' }", "German and an English" },
        { "'description': { 'de': 'Beispiel', 'en': 'Example' }", "'description': { 'de': 'Beispiel', 'en': ' ' }", "German and an English" },
        { "'category': 'partitioning'", "'category': 'partitionierung'", "unknown category" },
        { "'license': 'GPL-2.0-or-later'", "'license': 'GPL 2'", "neither an SPDX expression" },
        { "'license': 'GPL-2.0-or-later'", "'license': ''", "license is missing" },
        { "'homepage': 'https://example.org/'", "'homepage': 'http://example.org/'", "https address" },
        { "'homepage': 'https://example.org/'", "'homepage': 'https://user:secret@example.org/'", "https address" },
        { "'homepage': 'https://example.org/'", "'homepage': '/relative/page'", "https address" },
        { "'homepage': 'https://example.org/'", "'homepage': 'https://example.org/', 'notices': ['Hint.Wrong']", "Notice.* text key" },
        { "'homepage': 'https://example.org/'", "'homepage': 'https://example.org/', 'hints': ['Hint.a/b']", "Hint.* text key" },
        { "'homepage': 'https://example.org/'", "'homepage': 'https://example.org/', 'hints': ['Hint.']", "Hint.* text key" },
        { "'homepage': 'https://example.org/'", "'homepage': 'https://example.org/', 'includedTools': [' ']", "tool name" },
        { "'category': 'partitioning'", "'category': 'offline-password-reset'", "Notice.AuthorizedUseOnly" },
        { "'category': 'partitioning'", "'category': 'offline-password-reset', 'notices': ['Notice.Other']", "Notice.AuthorizedUseOnly" },
        { "'id': '1.0'", "'id': '../x'", "variant id" },
        { "'sha256': '" + Hash + "'", "'sha256': '3a4c'", "64 hex digits" },
        { "'sha256': '" + Hash + "'", "'sha256': '" + Hash[..^1] + "g'", "64 hex digits" },
        { "'hashSource': 'vendor',", "", "needs hashSource" },
        { "'hashSource': 'vendor'", "'hashSource': 'guess'", "needs hashSource" },
        { "'sha256': '" + Hash + "',", "", "hashSource without sha256" },
        { "'writeMode': 'iso-hybrid'", "'writeMode': 'none'", "needs a write mode" },
        { "'writeMode': 'iso-hybrid'", "'writeMode': 'burn'", "unknown write mode" },
        { "'writeMode': 'iso-hybrid',", "", "unknown write mode" },
        { "'size': 1000,", "'size': -5,", "size must be positive" },
        { "'architecture': 'x64'", "'architecture': 'mips'", "unknown architecture" },
        { "'size': 1000,", "'size': 1000, 'bios': 'maybe',", "bios must be" },
        { "'size': 1000,", "'size': 1000, 'packaging': 'rar',", "unknown packaging" },
        { "'size': 1000,", "'size': 1000, 'fileName': '../evil.iso',", "plain file name" },
        { "'size': 1000,", "'size': 1000, 'fileName': 'C:evil.iso',", "plain file name" },
        { "'size': 1000,", "'size': 1000, 'releaseDate': '2025-01-01', 'endOfSupport': '2024-12-31',", "support ends before" },
        { "'sources': [ { 'url': 'https://example.org/a.iso', 'priority': 1 } ]", "'sources': []", "exactly one of sources and manualUrl" },
        { "'sources': [", "'manualUrl': 'https://example.org/get', 'sources': [", "exactly one of sources and manualUrl" },
        { "'https://example.org/a.iso'", "'http://example.org/a.iso'", "https address" },
        { "'https://example.org/a.iso'", "'ftp://example.org/a.iso'", "https address" },
        { "'https://example.org/a.iso'", "'a.iso'", "https address" },
        { "'priority': 1", "'priority': 0", "invalid priority or location" },
        { "'priority': 1", "'priority': 1, 'location': 'Germany'", "invalid priority or location" },
        { "'sources': [", "'signature': { 'kind': 'open-pgp-checksums', 'url': 'https://example.org/s.gpg', 'fingerprint': '54C0821A48715DAFD61BFCAF667857D045599AFD' }, 'sources': [", "must name the file" },
        { "'sources': [", "'signature': { 'kind': 'open-pgp-detached', 'url': 'https://example.org/s.asc', 'fingerprint': '54C0' }, 'sources': [", "40 hex digits" },
        { "'sources': [", "'signature': { 'kind': 'x509', 'url': 'https://example.org/s.asc', 'fingerprint': '54C0821A48715DAFD61BFCAF667857D045599AFD' }, 'sources': [", "unknown signature kind" },
    };

    private static string Json(string text) => text.Replace('\'', '"');

    private static BootrixException Rejected(string json)
    {
        var ex = Assert.Throws<BootrixException>(() => RescueFixtures.Read(json));
        Assert.Equal(ErrorCode.CatalogUnavailable, ex.Code);
        return ex;
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void RejectsADocumentWithOneBrokenDetailAndNamesIt(string find, string replace, string expected)
    {
        find = Json(find);
        Assert.Contains(find, RescueFixtures.Minimal, StringComparison.Ordinal);

        var ex = Rejected(RescueFixtures.Minimal.Replace(find, Json(replace), StringComparison.Ordinal));

        Assert.Contains(expected, ex.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadsTheMinimalDocumentWithEveryField()
    {
        var document = RescueFixtures.Read(RescueFixtures.Minimal);

        Assert.Equal(3, document.Version);
        Assert.Equal(new DateOnly(2026, 10, 1), document.Issued);
        Assert.Equal(new DateOnly(2027, 4, 1), document.Expires);

        var entry = Assert.Single(document.Entries);
        Assert.Equal("sample", entry.Id);
        Assert.Equal(RescueCategory.Partitioning, entry.Category);
        Assert.Equal("GPL-2.0-or-later", entry.License);
        Assert.Equal(new Uri("https://example.org/"), entry.Homepage);
        Assert.Equal(new LocalizedText("Beispiel", "Example"), entry.Description);

        var variant = Assert.Single(entry.Variants);
        Assert.Equal(RescueWriteMode.IsoHybrid, variant.WriteMode);
        Assert.Equal("x64", variant.Architecture);
        Assert.Equal(1000, variant.Size);
        Assert.Equal(Hash, variant.Sha256);
        Assert.Equal(RescueHashOrigin.Vendor, variant.HashOrigin);
        Assert.Equal(new MirrorSource(new Uri("https://example.org/a.iso"), 1), Assert.Single(variant.Sources));
        Assert.Null(variant.ManualUrl);
        Assert.Equal(new RescueFirmware(BootSupport.Unknown, BootSupport.Unknown, BootSupport.Unknown), variant.Firmware);
        Assert.Equal(RescuePackaging.None, variant.Packaging);
    }

    [Fact]
    public void EnumNamesAreKebabCaseInTheFile()
    {
        Assert.Equal("win-pe-repair", RescueNames.Of(RescueCategory.WinPeRepair));
        Assert.Equal("offline-password-reset", RescueNames.Of(RescueCategory.OfflinePasswordReset));
        Assert.Equal("linux-live-rescue", RescueNames.Of(RescueCategory.LinuxLiveRescue));
        Assert.Equal("raw-dd", RescueNames.Of(RescueWriteMode.RawDd));
        Assert.Equal("iso-hybrid", RescueNames.Of(RescueWriteMode.IsoHybrid));
        Assert.Equal("windows-pe", RescueNames.Of(RescueWriteMode.WindowsPe));
        Assert.Equal("grub-chain", RescueNames.Of(RescueWriteMode.GrubChain));
        Assert.Equal("open-pgp-checksums", RescueNames.Of(RescueSignatureKind.OpenPgpChecksums));
    }

    [Fact]
    public void EveryCategoryAndWriteModeCanBeWrittenInTheFile()
    {
        foreach (var category in Enum.GetValues<RescueCategory>())
        {
            var notice = category == RescueCategory.OfflinePasswordReset ? "'notices': ['Notice.AuthorizedUseOnly']," : string.Empty;
            var json = RescueFixtures.Minimal.Replace(
                Json("'category': 'partitioning',"),
                Json($"'category': '{RescueNames.Of(category)}', {notice}"),
                StringComparison.Ordinal);

            Assert.Equal(category, RescueFixtures.Read(json).Entries[0].Category);
        }

        foreach (var mode in Enum.GetValues<RescueWriteMode>().Where(m => m != RescueWriteMode.None))
        {
            var json = RescueFixtures.Minimal.Replace(Json("'iso-hybrid'"), Json($"'{RescueNames.Of(mode)}'"), StringComparison.Ordinal);

            Assert.Equal(mode, RescueFixtures.Read(json).Entries[0].Variants[0].WriteMode);
        }
    }

    [Fact]
    public void PasswordToolWithTheNoticeIsAccepted()
    {
        var json = RescueFixtures.Minimal.Replace(
            Json("'category': 'partitioning'"),
            Json("'category': 'offline-password-reset', 'notices': ['Notice.AuthorizedUseOnly']"),
            StringComparison.Ordinal);

        var entry = RescueFixtures.Read(json).Entries[0];

        Assert.Equal([RescueNotices.AuthorizedUseOnly], entry.Notices);
    }

    [Fact]
    public void ManualVariantWithoutSourcesIsAcceptedAndMayKeepAHashForTheFileTheUserPicks()
    {
        var json = RescueFixtures.Minimal.Replace(
            Json("'sources': [ { 'url': 'https://example.org/a.iso', 'priority': 1 } ]"),
            Json("'manualUrl': 'https://example.org/get'"),
            StringComparison.Ordinal);

        var variant = RescueFixtures.Read(json).Entries[0].Variants[0];

        Assert.Equal(new Uri("https://example.org/get"), variant.ManualUrl);
        Assert.Empty(variant.Sources);
        Assert.Equal(Hash, variant.Sha256);
    }

    [Fact]
    public void HashesAndFingerprintsAreNormalized()
    {
        var json = RescueFixtures.Minimal
            .Replace(Hash, Hash.ToUpperInvariant(), StringComparison.Ordinal)
            .Replace(
                Json("'sources': ["),
                Json("'signature': { 'kind': 'open-pgp-detached', 'url': 'https://example.org/s.asc', 'fingerprint': '54c0 821a 4871 5daf d61b  fcaf 6678 57d0 4559 9afd' }, 'sources': ["),
                StringComparison.Ordinal)
            .Replace(Json("'priority': 1"), Json("'priority': 2, 'location': 'de'"), StringComparison.Ordinal);

        var variant = RescueFixtures.Read(json).Entries[0].Variants[0];

        Assert.Equal(Hash, variant.Sha256);
        Assert.Equal("54C0821A48715DAFD61BFCAF667857D045599AFD", variant.Signature!.Fingerprint);
        Assert.Equal(new MirrorSource(new Uri("https://example.org/a.iso"), 2, "DE"), variant.Sources[0]);
    }

    [Fact]
    public void SeveralSourcesNeedADigestBecauseNothingElseWouldNoticeAWrongMirror()
    {
        var withoutDigest = RescueFixtures.Minimal
            .Replace(Json("'sha256': '" + Hash + "',"), string.Empty, StringComparison.Ordinal)
            .Replace(Json("'hashSource': 'vendor',"), string.Empty, StringComparison.Ordinal);
        const string oneSource = "'sources': [ { 'url': 'https://example.org/a.iso', 'priority': 1 } ]";
        const string twoSources = "'sources': [ { 'url': 'https://example.org/a.iso', 'priority': 1 }, { 'url': 'https://mirror.example.net/a.iso', 'priority': 2 } ]";

        Assert.Single(RescueFixtures.Read(withoutDigest).Entries[0].Variants[0].Sources);

        var ex = Rejected(withoutDigest.Replace(Json(oneSource), Json(twoSources), StringComparison.Ordinal));
        Assert.Contains("several sources need a SHA-256", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameSourceTwiceIsRejected()
    {
        const string twice = "'sources': [ { 'url': 'https://example.org/a.iso' }, { 'url': 'https://example.org/a.iso' } ]";
        var json = RescueFixtures.Minimal.Replace(
            Json("'sources': [ { 'url': 'https://example.org/a.iso', 'priority': 1 } ]"),
            Json(twice),
            StringComparison.Ordinal);

        Assert.Contains("listed twice", Rejected(json).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEntriesIsRejected()
    {
        var node = JsonNode.Parse(RescueFixtures.Minimal)!;
        node["entries"] = new JsonArray();

        Assert.Contains("no entries", Rejected(node.ToJsonString()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedIdsAreRejectedForEntriesAndForVariants()
    {
        var entries = JsonNode.Parse(RescueFixtures.Minimal)!;
        entries["entries"]!.AsArray().Add(entries["entries"]![0]!.DeepClone());
        Assert.Contains("entry id 'sample' is used twice", Rejected(entries.ToJsonString()).Detail, StringComparison.Ordinal);

        var variants = JsonNode.Parse(RescueFixtures.Minimal)!;
        var list = variants["entries"]![0]!["variants"]!.AsArray();
        list.Add(list[0]!.DeepClone());
        Assert.Contains("variant id '1.0' is used twice", Rejected(variants.ToJsonString()).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyOneVariantCanBeRecommended()
    {
        var node = JsonNode.Parse(RescueFixtures.Minimal)!;
        var list = node["entries"]![0]!["variants"]!.AsArray();
        list[0]!["recommended"] = true;
        var second = list[0]!.DeepClone();
        second["id"] = "2.0";
        list.Add(second);

        Assert.Contains("only one variant can be recommended", Rejected(node.ToJsonString()).Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{ 'schemaVersion': 1, 'version': 1, 'issued': '2026-01-01', 'expires': '2026-06-01', 'entries': [ null ] }")]
    [InlineData("{ 'schemaVersion': 'one' }")]
    public void GarbageIsRejectedAsACatalogProblem(string json)
    {
        Rejected(Json(json));
    }

    [Fact]
    public void CommentsAndTrailingCommasAreTolerated()
    {
        var json = RescueFixtures.Minimal
            .Replace(Json("'schemaVersion': 1,"), Json("// the schema\n  'schemaVersion': 1,"), StringComparison.Ordinal)
            .Replace(Json("'priority': 1 }"), Json("'priority': 1 },"), StringComparison.Ordinal);

        Assert.Equal(3, RescueFixtures.Read(json).Version);
    }

    [Fact]
    public void ReadsFromAJsonElementAsDeliveredInAManifestPayload()
    {
        using var document = JsonDocument.Parse(RescueFixtures.Minimal);

        Assert.Equal(3, RescueCatalogReader.Read(document.RootElement).Version);

        using var array = JsonDocument.Parse("[]");
        Assert.Equal(ErrorCode.CatalogUnavailable, Assert.Throws<BootrixException>(() => RescueCatalogReader.Read(array.RootElement)).Code);
        Assert.Equal(ErrorCode.CatalogUnavailable, Assert.Throws<BootrixException>(() => RescueCatalogReader.Read(default(JsonElement))).Code);
    }

    [Fact]
    public void LocalizedTextFollowsTheLanguageAndFallsBackToEnglish()
    {
        var text = new LocalizedText("Hallo", "Hello");

        Assert.Equal("Hallo", text.For(CultureInfo.GetCultureInfo("de-AT")));
        Assert.Equal("Hello", text.For(CultureInfo.GetCultureInfo("en-GB")));
        Assert.Equal("Hello", text.For(CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("Hello", text.For(CultureInfo.InvariantCulture));
    }
}
