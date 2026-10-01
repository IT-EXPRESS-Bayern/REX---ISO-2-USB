// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Planning;

internal static class SizeText
{
    private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>A size for messages, e.g. "7.45 GiB"; invariant formatting so tests and logs read the same everywhere.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes.ToString(CultureInfo.InvariantCulture)} B";
        }

        var value = (double)bytes;
        var unit = 0;

        // Compare the rounded value, so 1023.999 GiB reads as 1 TiB rather than 1024 GiB.
        while (Math.Round(value, 2) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture)} {Units[unit]}";
    }
}
