// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Fat;

public static class FatVolumeId
{
    /// <summary>
    /// The serial derived from the clock the way format.com and fat32format do it. The low word is
    /// day plus month, seconds and hundredths, the high word is year plus hour and minute; month,
    /// seconds and hour are shifted into the upper byte of their word.
    /// </summary>
    public static uint FromTime(DateTimeOffset time)
    {
        var low = (ushort)(time.Day + (time.Month << 8) + time.Millisecond / 10 + (time.Second << 8));
        var high = (ushort)(time.Minute + (time.Hour << 8) + time.Year);
        return ((uint)high << 16) | low;
    }
}
