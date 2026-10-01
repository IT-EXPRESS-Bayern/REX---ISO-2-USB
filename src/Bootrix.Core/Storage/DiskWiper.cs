// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

/// <summary>
/// Destroys the places where old partition tables and file system signatures live. The head covers
/// the MBR, the primary GPT and the start of the first partition; the tail covers the backup GPT.
/// A stale backup GPT is the usual reason why a "wiped" stick suddenly shows its old partitions again.
/// </summary>
public static class DiskWiper
{
    public const long DefaultHeadBytes = 8L * 1024 * 1024;
    public const long DefaultTailBytes = 1L * 1024 * 1024;

    public static void WipeTables(IBlockDevice device, long headBytes = DefaultHeadBytes, long tailBytes = DefaultTailBytes)
    {
        var head = RoundDown(Math.Min(headBytes, device.Length), device.SectorSize);
        var tail = RoundDown(Math.Min(tailBytes, device.Length - head), device.SectorSize);

        using var zeros = new AlignedBuffer(1024 * 1024, device.BufferAlignment);
        ZeroRange(device, zeros, 0, head);
        ZeroRange(device, zeros, RoundDown(device.Length, device.SectorSize) - tail, tail);
        device.Flush();
    }

    /// <summary>Zeroes one range, e.g. the first megabytes of a partition that is about to be formatted.</summary>
    public static void ZeroRange(IBlockDevice device, long offset, long length)
    {
        using var zeros = new AlignedBuffer(1024 * 1024, device.BufferAlignment);
        ZeroRange(device, zeros, offset, length);
        device.Flush();
    }

    private static void ZeroRange(IBlockDevice device, AlignedBuffer zeros, long offset, long length)
    {
        var span = zeros.GetSpan();
        var end = offset + length;
        while (offset < end)
        {
            var take = (int)Math.Min(span.Length, end - offset);
            take -= take % device.SectorSize;
            if (take == 0)
            {
                break;
            }

            device.Write(offset, span[..take]);
            offset += take;
        }
    }

    private static long RoundDown(long value, int multiple) => value / multiple * multiple;
}
