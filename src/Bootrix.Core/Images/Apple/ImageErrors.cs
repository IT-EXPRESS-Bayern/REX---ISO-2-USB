// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Images.Apple;

internal static class ImageErrors
{
    public static BootrixException Corrupt(string reason) =>
        new(ErrorCode.ImageCorrupt, reason) { Arguments = [reason] };

    public static BootrixException Unsupported(string what) =>
        new(ErrorCode.ImageUnsupported, what) { Arguments = [what] };

    public static BootrixException Unreadable(string reason, Exception? inner = null) =>
        new(ErrorCode.ImageUnreadable, reason, inner) { Arguments = [reason] };

    public static BootrixException Truncated(long expected, long actual) =>
        new(ErrorCode.ImageTruncated, $"expected {expected} bytes, found {actual}")
        {
            Arguments = [FormatSize(expected), FormatSize(actual)],
        };

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }
}
