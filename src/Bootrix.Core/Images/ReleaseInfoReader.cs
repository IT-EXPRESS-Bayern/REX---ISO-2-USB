// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Images;

/// <summary>Reads the product and version line distributions leave in small text files of their install media.</summary>
internal static partial class ReleaseInfoReader
{
    public static string? Read(Func<string, string?> readText)
    {
        // Debian and Ubuntu families: ".disk/info" holds one descriptive line.
        if (FirstLine(readText(".disk/info")) is { } disk)
        {
            return disk;
        }

        // Fedora and Red Hat family: INI file with name and version in [general].
        if (readText(".treeinfo") is { } treeInfo)
        {
            var name = TreeInfoValue(treeInfo, "name") ?? TreeInfoValue(treeInfo, "family");
            var version = TreeInfoValue(treeInfo, "version");
            if (name is not null)
            {
                return version is null ? name : $"{name} {version}";
            }
        }

        // SUSE media: "PRODUCT openSUSE-Tumbleweed" / "VERSION 20240101".
        if (readText("content") is { } content && SuseProduct().Match(content) is { Success: true } product)
        {
            return product.Groups[1].Value.Trim();
        }

        return FirstLine(readText("Clonezilla-Live-Version")) ?? FirstLine(readText("GParted-Live-Version"));
    }

    private static string? FirstLine(string? text)
    {
        var line = text?.Split('\n', 2)[0].Trim().TrimEnd('\0');
        return string.IsNullOrEmpty(line) ? null : line;
    }

    private static string? TreeInfoValue(string text, string key)
    {
        var match = Regex.Match(text, $@"^\s*{key}\s*=\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^PRODUCT\s+(.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SuseProduct();
}
