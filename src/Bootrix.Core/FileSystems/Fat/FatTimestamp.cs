// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Fat;

internal static class FatTimestamp
{
    /// <summary>The DOS date and time words of a directory entry; years before 1980 are not representable.</summary>
    public static (ushort Date, ushort Time) Encode(DateTimeOffset time)
    {
        var year = Math.Clamp(time.Year, 1980, 2107);
        var date = (ushort)(((year - 1980) << 9) | (time.Month << 5) | time.Day);
        var clock = (ushort)((time.Hour << 11) | (time.Minute << 5) | (time.Second / 2));
        return (date, clock);
    }
}
