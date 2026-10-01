// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Bootrix.Core.Images.Wim;

internal static class WimXmlParser
{
    private const int MaxCharacters = 32 * 1024 * 1024;

    public static (IReadOnlyList<WimEdition> Editions, long? TotalBytes) Parse(string xml)
    {
        // The data starts with a byte order mark and may be padded with NULs by some writers.
        var text = xml.Trim('﻿', '\0', ' ', '\r', '\n');
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
        };

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw ImageErrors.Unreadable("The XML data of the WIM is malformed: " + ex.Message, ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "WIM")
        {
            throw ImageErrors.Unreadable("The XML data of the WIM has no WIM element.");
        }

        var editions = root.Elements("IMAGE").Select(ParseImage).OrderBy(edition => edition.Index).ToList();
        var totalBytes = ParseLong(root.Element("TOTALBYTES")?.Value);
        return (editions, totalBytes > 0 ? totalBytes : null);
    }

    private static WimEdition ParseImage(XElement image)
    {
        var windows = image.Element("WINDOWS");
        var version = windows?.Element("VERSION");
        var archCode = ParseInt(windows?.Element("ARCH")?.Value);
        var languages = windows?.Element("LANGUAGES");

        return new WimEdition
        {
            Index = (int)ParseLong(image.Attribute("INDEX")?.Value),
            Name = Text(image, "NAME"),
            Description = Text(image, "DESCRIPTION"),
            DisplayName = Text(image, "DISPLAYNAME"),
            EditionId = Text(windows, "EDITIONID"),
            InstallationType = Text(windows, "INSTALLATIONTYPE"),
            ProductName = Text(windows, "PRODUCTNAME"),
            TotalBytes = ParseLong(image.Element("TOTALBYTES")?.Value),
            FileCount = ParseLong(image.Element("FILECOUNT")?.Value),
            DirectoryCount = ParseLong(image.Element("DIRCOUNT")?.Value),
            ArchCode = archCode,
            Arch = archCode is { } code ? MapArchitecture(code) : WindowsArch.Unknown,
            MajorVersion = ParseInt(version?.Element("MAJOR")?.Value) ?? 0,
            MinorVersion = ParseInt(version?.Element("MINOR")?.Value) ?? 0,
            Build = ParseInt(version?.Element("BUILD")?.Value) ?? 0,
            ServicePackBuild = ParseInt(version?.Element("SPBUILD")?.Value) ?? 0,
            ServicePackLevel = ParseInt(version?.Element("SPLEVEL")?.Value) ?? 0,
            Languages = [.. languages?.Elements("LANGUAGE").Select(language => language.Value.Trim()) ?? []],
            DefaultLanguage = Text(languages, "DEFAULT"),
            Created = ParseFileTime(image.Element("CREATIONTIME")),
            LastModified = ParseFileTime(image.Element("LASTMODIFICATIONTIME")),
        };
    }

    /// <summary>PROCESSOR_ARCHITECTURE_* values as written by DISM and wimlib.</summary>
    internal static WindowsArch MapArchitecture(int code) => code switch
    {
        0 => WindowsArch.X86,
        5 => WindowsArch.Arm,
        9 => WindowsArch.X64,
        12 => WindowsArch.Arm64,
        _ => WindowsArch.Unknown,
    };

    private static string? Text(XElement? parent, string name)
    {
        var value = parent?.Element(name)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static long ParseLong(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    /// <summary>Times are stored as the two halves of a FILETIME, each as "0x" followed by hex digits.</summary>
    private static DateTimeOffset? ParseFileTime(XElement? element)
    {
        if (!TryParseHex(element?.Element("HIGHPART")?.Value, out var high)
            || !TryParseHex(element?.Element("LOWPART")?.Value, out var low))
        {
            return null;
        }

        var ticks = (high << 32) | low;
        if (ticks == 0 || ticks > (ulong)DateTime.MaxValue.ToFileTimeUtc())
        {
            return null;
        }

        return new DateTimeOffset(DateTime.FromFileTimeUtc((long)ticks), TimeSpan.Zero);
    }

    private static bool TryParseHex(string? value, out ulong result)
    {
        result = 0;
        var text = value?.Trim();
        if (text is null)
        {
            return false;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);
    }
}
