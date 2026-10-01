// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>One ESD image of the Media Creation Tool catalog; editions that share a file are folded into <see cref="Editions"/>.</summary>
internal sealed record CatalogEsd(
    string FileName,
    string Language,
    string LanguageCode,
    IReadOnlyList<string> Editions,
    string Architecture,
    long Size,
    FileHash Sha1,
    Uri Url,
    EsdChannel Channel,
    string? Build,
    string? Release,
    DateOnly? Built);

/// <summary>Which of Microsoft's three media families a file belongs to; the file name says so.</summary>
internal enum EsdChannel
{
    Other,
    Consumer,
    Business,
    China,
}

/// <summary>Reads <c>products.xml</c>, the list the Media Creation Tool works from, as a pure function over its bytes.</summary>
internal static partial class ProductsCatalog
{
    /// <summary>
    /// Entries that miss a field or carry values that make no sense (digest of the wrong length, relative address)
    /// are left out; a catalog without a single usable entry is an error, which is how a changed schema shows up.
    /// </summary>
    public static IReadOnlyList<CatalogEsd> Parse(byte[] xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;
        try
        {
            using var stream = new MemoryStream(xml);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"products.xml is not well-formed: {ex.Message}", ex);
        }

        // The catalog repeats one file for every edition it contains; fold those entries back into one image.
        var images = document.Descendants("File")
            .Select(ParseFile)
            .OfType<CatalogEsd>()
            .GroupBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First() with { Editions = [.. g.SelectMany(e => e.Editions).Distinct(StringComparer.Ordinal)] })
            .ToList();

        return images.Count > 0 ? images : throw new InvalidDataException("products.xml lists no usable image.");
    }

    private static CatalogEsd? ParseFile(XElement file)
    {
        var fileName = Text(file, "FileName");
        var languageCode = Text(file, "LanguageCode");
        var architecture = Text(file, "Architecture");

        if (fileName is null || languageCode is null || architecture is null
            || !long.TryParse(Text(file, "Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0
            || !FileHash.TryCreate(HashKind.Sha1, Text(file, "Sha1"), out var sha1)
            || !Uri.TryCreate(Text(file, "FilePath"), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https"))
        {
            return null;
        }

        var edition = Text(file, "Edition");
        var name = FileNamePattern().Match(fileName);

        return new CatalogEsd(
            fileName,
            Text(file, "Language") ?? languageCode,
            LanguageTags.Normalize(languageCode),
            edition is null ? [] : [edition],
            NormalizeArchitecture(architecture),
            size,
            sha1!,
            url,
            ChannelOf(fileName),
            name.Success ? $"{name.Groups["build"].Value}.{name.Groups["revision"].Value}" : null,
            name.Groups["release"] is { Success: true } release ? release.Value.ToUpperInvariant() : null,
            name.Success ? ParseDate(name.Groups["date"].Value) : null);
    }

    private static string? Text(XElement parent, string name)
    {
        var value = parent.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string NormalizeArchitecture(string value) => value.ToLowerInvariant() switch
    {
        "amd64" => "x64",
        "i386" => "x86",
        var other => other,
    };

    private static EsdChannel ChannelOf(string fileName) =>
        ChannelPattern().Match(fileName).Groups["channel"].Value.ToUpperInvariant() switch
        {
            "CONSUMER" => EsdChannel.Consumer,
            "BUSINESS" => EsdChannel.Business,
            "CHINA" => EsdChannel.China,
            _ => EsdChannel.Other,
        };

    // The build stamp is "yyMMdd-HHmm"; only the date matters for display.
    private static DateOnly? ParseDate(string stamp) =>
        DateOnly.TryParseExact("20" + stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    [GeneratedRegex(@"^(?<build>\d{4,6})\.(?<revision>\d+)\.(?<date>\d{6})-\d{4}\.(?:(?<release>\d{2}h[12])_)?", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"_CLIENT(?<channel>CONSUMER|BUSINESS|CHINA)_(?:RET|VOL)_", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();
}
