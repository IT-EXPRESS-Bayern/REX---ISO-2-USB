// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Xml;

namespace Bootrix.Core.Images.Apple;

/// <summary>
/// Reads the XML flavour of Apple property lists into dictionaries, lists, strings, numbers,
/// booleans and byte arrays. DTDs are ignored and never resolved.
/// </summary>
internal static class PlistReader
{
    private const int MaxDepth = 16;

    public static Dictionary<string, object?> ParseDictionary(ReadOnlySpan<byte> xml)
    {
        if (xml.StartsWith("bplist"u8))
        {
            throw ImageErrors.Unsupported("binary property list");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = xml.Length + 1024L,
        };

        try
        {
            using var stream = new MemoryStream(xml.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            if (!reader.ReadToFollowing("plist"))
            {
                throw ImageErrors.Corrupt("property list has no plist element");
            }

            reader.Read();
            return ReadValue(reader, 0) as Dictionary<string, object?>
                ?? throw ImageErrors.Corrupt("property list root is not a dictionary");
        }
        catch (XmlException ex)
        {
            throw ImageErrors.Corrupt("property list is not valid XML: " + ex.Message);
        }
        catch (FormatException ex)
        {
            throw ImageErrors.Corrupt("property list contains invalid data: " + ex.Message);
        }
    }

    private static object? ReadValue(XmlReader reader, int depth)
    {
        if (depth > MaxDepth)
        {
            throw ImageErrors.Corrupt("property list is nested too deeply");
        }

        if (reader.NodeType != XmlNodeType.Element)
        {
            throw ImageErrors.Corrupt("unexpected content in property list");
        }

        switch (reader.Name)
        {
            case "dict":
                return ReadDictionary(reader, depth);
            case "array":
                return ReadArray(reader, depth);
            case "string":
            case "date":
                return reader.ReadElementContentAsString();
            case "integer":
                return ParseInteger(reader.ReadElementContentAsString());
            case "real":
                return double.Parse(reader.ReadElementContentAsString(), CultureInfo.InvariantCulture);
            case "data":
                return Convert.FromBase64String(reader.ReadElementContentAsString());
            case "true":
                reader.Skip();
                return true;
            case "false":
                reader.Skip();
                return false;
            default:
                throw ImageErrors.Corrupt($"unknown property list element <{reader.Name}>");
        }
    }

    private static Dictionary<string, object?> ReadDictionary(XmlReader reader, int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (reader.IsEmptyElement)
        {
            reader.Skip();
            return result;
        }

        reader.ReadStartElement("dict");
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.EOF || reader.NodeType != XmlNodeType.Element || reader.Name != "key")
            {
                throw ImageErrors.Corrupt("dictionary entry without key");
            }

            var key = reader.ReadElementContentAsString();
            result[key] = ReadValue(reader, depth + 1);
        }

        reader.ReadEndElement();
        return result;
    }

    private static List<object?> ReadArray(XmlReader reader, int depth)
    {
        var result = new List<object?>();
        if (reader.IsEmptyElement)
        {
            reader.Skip();
            return result;
        }

        reader.ReadStartElement("array");
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (reader.EOF)
            {
                throw ImageErrors.Corrupt("property list ends inside an array");
            }

            result.Add(ReadValue(reader, depth + 1));
        }

        reader.ReadEndElement();
        return result;
    }

    private static long ParseInteger(string text)
    {
        if (long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw ImageErrors.Corrupt("property list integer out of range");
    }
}
