// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Workshop.Capture;

/// <summary>
/// Reads the [Version] section of a driver package's INF file. Listing the driver store through its INF files avoids parsing
/// the translated output of `pnputil /enum-drivers`, whose labels change with the Windows language.
/// </summary>
public static partial class InfFileParser
{
    private static readonly string[] DateFormats = ["MM/dd/yyyy", "M/d/yyyy"];

    /// <summary>Returns null when the file has no [Version] section.</summary>
    public static ThirdPartyDriver? Parse(string publishedName, ReadOnlySpan<byte> content)
    {
        var version = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        var sawVersion = false;

        foreach (var rawLine in Decode(content).Split('\n'))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                sawVersion |= section.Equals("Version", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = Unquote(line[(separator + 1)..].Trim());
            if (section.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                version[key] = value;
            }
            else if (section.Equals("Strings", StringComparison.OrdinalIgnoreCase))
            {
                strings[key] = value;
            }
        }

        if (!sawVersion)
        {
            return null;
        }

        var (date, versionText) = SplitDriverVersion(version.GetValueOrDefault("DriverVer"));
        return new ThirdPartyDriver
        {
            PublishedName = publishedName,
            Provider = Expand(version.GetValueOrDefault("Provider"), strings),
            ClassName = Expand(version.GetValueOrDefault("Class"), strings),
            Version = versionText,
            Date = date,
            CatalogFile = Expand(version.GetValueOrDefault("CatalogFile"), strings),
        };
    }

    /// <summary>
    /// INF files are UTF-16 with a byte order mark in the driver store but older packages are ANSI, and some tools write UTF-8.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return Encoding.Unicode.GetString(content[2..]);
        }

        if (content.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return Encoding.BigEndianUnicode.GetString(content[2..]);
        }

        if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            return Encoding.UTF8.GetString(content[3..]);
        }

        return Encoding.Latin1.GetString(content);
    }

    private static (DateOnly? Date, string? Version) SplitDriverVersion(string? driverVer)
    {
        if (string.IsNullOrWhiteSpace(driverVer))
        {
            return (null, null);
        }

        // DriverVer = mm/dd/yyyy[,w.x.y.z]
        var parts = driverVer.Split(',', 2, StringSplitOptions.TrimEntries);
        DateOnly? date = DateOnly.TryParseExact(parts[0], DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        return (date, parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
    }

    /// <summary>Resolves %token% against the [Strings] section; an unknown token is returned as written.</summary>
    private static string? Expand(string? value, Dictionary<string, string> strings)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return Token().Replace(value, m => strings.GetValueOrDefault(m.Groups[1].Value, m.Value));
    }

    private static string StripComment(string line)
    {
        // A semicolon inside quotes belongs to the value.
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ';' && !quoted)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex Token();
}
