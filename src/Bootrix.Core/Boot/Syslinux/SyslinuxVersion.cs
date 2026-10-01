// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Boot.Syslinux;

/// <summary>
/// The version a Syslinux-family binary announces in its banner, e.g. "ISOLINUX 6.04 6.04-pre1" or
/// "ISOLINUX 6.03 2014-10-06". Distributions rebuild Syslinux from git snapshots and put a build date
/// where upstream puts a release tag, so the tag is kept as it is and only major and minor are compared.
/// </summary>
public readonly partial record struct SyslinuxVersion(int Major, int Minor, string Tag)
{
    // Rufus starts at offset 64 because the first bytes are boot code; the banner is plain ASCII further in.
    private const int ScanStart = 64;

    public override string ToString() => Tag.Length == 0 ? $"{Major}.{Minor:00}" : $"{Major}.{Minor:00} {Tag}";

    public bool SameRelease(SyslinuxVersion other) => Major == other.Major && Minor == other.Minor;

    /// <summary>Finds the version banner in a binary such as isolinux.bin; null when it carries none.</summary>
    public static SyslinuxVersion? FromBinary(ReadOnlySpan<byte> binary)
    {
        if (binary.Length <= ScanStart)
        {
            return null;
        }

        var text = Encoding.Latin1.GetString(binary[ScanStart..]);
        return Parse(text);
    }

    public static SyslinuxVersion? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (Match match in Banner().Matches(text))
        {
            if (!int.TryParse(match.Groups["major"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                || !int.TryParse(match.Groups["minor"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
            {
                continue;
            }

            return new SyslinuxVersion(major, minor, match.Groups["tag"].Value);
        }

        return null;
    }

    // The tag is whatever printable text follows up to the next blank; "*" (Arch) and path characters are dropped
    // because the value ends up in log lines and file names of caches.
    [GeneratedRegex(@"(?:ISO|SYS|EXT|PXE)LINUX (?<major>\d{1,2})\.(?<minor>\d{2})(?:[ -](?<tag>[A-Za-z0-9._+-]{1,24}))?", RegexOptions.CultureInvariant)]
    private static partial Regex Banner();
}
