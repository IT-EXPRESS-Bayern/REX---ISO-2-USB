// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Text;

/// <summary>
/// Formats sizes the way the Windows Explorer does: powers of 1024 with the short unit names,
/// so a "16 GB" stick shows up as 14,4 GB and matches what the user sees in the file manager.
/// </summary>
public static class ByteSize
{
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return bytes switch
        {
            < 0 => string.Create(culture, $"-{Format(-bytes, culture)}"),
            >= 1L << 40 => string.Create(culture, $"{bytes / (double)(1L << 40):0.##} TB"),
            >= 1L << 30 => string.Create(culture, $"{bytes / (double)(1L << 30):0.##} GB"),
            >= 1L << 20 => string.Create(culture, $"{bytes / (double)(1L << 20):0.#} MB"),
            >= 1L << 10 => string.Create(culture, $"{bytes / 1024.0:0} KB"),
            _ => string.Create(culture, $"{bytes} B"),
        };
    }

    public static string FormatRate(double bytesPerSecond, CultureInfo? culture = null) =>
        bytesPerSecond < 1 ? "" : Format((long)bytesPerSecond, culture) + "/s";

    /// <summary>Remaining time as "1:23:05" or "4:12"; empty while the estimate is not stable yet.</summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } value || value < TimeSpan.Zero)
        {
            return "";
        }

        return value.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{value.Minutes}:{value.Seconds:00}");
    }
}
