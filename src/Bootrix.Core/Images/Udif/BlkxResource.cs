// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Images.Udif;

/// <summary>One partition entry of the image: its name and the raw bytes of its block table.</summary>
internal sealed record BlkxResource(int Id, string Name, uint Attributes, byte[] Data)
{
    /// <summary>Reads the <c>resource-fork/blkx</c> array of the XML property list stored behind the data fork.</summary>
    public static List<BlkxResource> ReadPlist(ReadOnlySpan<byte> xml)
    {
        var root = PlistReader.ParseDictionary(xml);
        if (root.GetValueOrDefault("resource-fork") is not Dictionary<string, object?> fork
            || fork.GetValueOrDefault("blkx") is not List<object?> items)
        {
            throw ImageErrors.Corrupt("property list has no blkx resources");
        }

        var result = new List<BlkxResource>(items.Count);
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> dict || dict.GetValueOrDefault("Data") is not byte[] data)
            {
                throw ImageErrors.Corrupt("blkx entry without data");
            }

            var name = dict.GetValueOrDefault("Name") as string
                ?? dict.GetValueOrDefault("CFName") as string
                ?? string.Empty;
            result.Add(new BlkxResource(ParseId(dict.GetValueOrDefault("ID")), name, ParseAttributes(dict.GetValueOrDefault("Attributes")), data));
        }

        return result;
    }

    private static int ParseId(object? value) => value switch
    {
        long number => (int)Math.Clamp(number, int.MinValue, int.MaxValue),
        string text when int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) => id,
        _ => 0,
    };

    private static uint ParseAttributes(object? value)
    {
        if (value is not string text)
        {
            return 0;
        }

        text = text.Trim();
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return uint.TryParse(hex ? text[2..] : text, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var attributes)
            ? attributes
            : 0;
    }
}
