// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>Little-endian bit order as used by the ext block and inode bitmaps (bit 0 is the lowest bit of byte 0).</summary>
internal static class ExtBitmap
{
    public static bool IsSet(ReadOnlySpan<byte> bitmap, int bit) => (bitmap[bit >> 3] & (1 << (bit & 7))) != 0;

    public static void Set(Span<byte> bitmap, int bit) => bitmap[bit >> 3] |= (byte)(1 << (bit & 7));

    public static void Clear(Span<byte> bitmap, int bit) => bitmap[bit >> 3] &= (byte)~(1 << (bit & 7));

    public static void SetRange(Span<byte> bitmap, int from, int count)
    {
        for (var bit = from; bit < from + count; bit++)
        {
            Set(bitmap, bit);
        }
    }

    /// <summary>Index of the first clear bit in [<paramref name="from"/>, <paramref name="limit"/>), or -1.</summary>
    public static int FindClear(ReadOnlySpan<byte> bitmap, int from, int limit)
    {
        var bit = from;
        while (bit < limit)
        {
            if ((bit & 7) == 0 && bitmap[bit >> 3] == 0xFF)
            {
                bit += 8;
                continue;
            }

            if (!IsSet(bitmap, bit))
            {
                return bit;
            }

            bit++;
        }

        return -1;
    }
}
