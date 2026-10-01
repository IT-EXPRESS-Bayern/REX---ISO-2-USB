// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Numerics;

namespace Bootrix.Core.Library;

/// <summary>
/// Orders the version strings of catalogs ("24.04.3", "13.02", "2.06s4", "25H2", "3.3.3-37") without knowing their
/// scheme: runs of digits compare as numbers, runs of letters as text, separators are ignored.
/// </summary>
internal sealed class VersionOrder : IComparer<string?>
{
    // A trailing "rc1" or "beta" makes a version older than the same version without it; any other suffix ("s4", ".1") makes it newer.
    private static readonly string[] PreRelease = ["alpha", "beta", "rc", "pre", "preview", "dev", "test", "nightly"];

    public static VersionOrder Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            // A missing version is older than any version.
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var left = Tokens(x);
        var right = Tokens(y);

        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var result = CompareToken(left[i], right[i]);
            if (result != 0)
            {
                return result;
            }
        }

        if (left.Count == right.Count)
        {
            return 0;
        }

        var longer = left.Count > right.Count ? left : right;
        var extra = longer[Math.Min(left.Count, right.Count)];
        var longerIsNewer = IsNumber(extra) || !PreRelease.Contains(extra, StringComparer.OrdinalIgnoreCase);
        return (left.Count > right.Count ? 1 : -1) * (longerIsNewer ? 1 : -1);
    }

    private static int CompareToken(string a, string b)
    {
        var aNumber = IsNumber(a);
        var bNumber = IsNumber(b);

        if (aNumber && bNumber)
        {
            return BigInteger.Parse(a, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(b, CultureInfo.InvariantCulture));
        }

        if (aNumber != bNumber)
        {
            return aNumber ? 1 : -1;
        }

        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumber(string token) => char.IsAsciiDigit(token[0]);

    private static List<string> Tokens(string version)
    {
        var tokens = new List<string>();
        var start = -1;

        for (var i = 0; i <= version.Length; i++)
        {
            var kind = i < version.Length ? Kind(version[i]) : 0;
            if (start >= 0 && (i == version.Length || kind != Kind(version[start])))
            {
                tokens.Add(version[start..i]);
                start = -1;
            }

            if (start < 0 && kind != 0)
            {
                start = i;
            }
        }

        return tokens;
    }

    /// <summary>1 for digits, 2 for letters, 0 for everything that only separates.</summary>
    private static int Kind(char c) => char.IsAsciiDigit(c) ? 1 : char.IsAsciiLetter(c) ? 2 : 0;
}
