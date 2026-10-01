// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Writing.Restore;

/// <summary>
/// Clears everything that can make a stick look like it still holds an image: the start of the disk, the end of
/// the disk and the place where the old backup GPT lies. A hybrid image written raw has its backup GPT behind the
/// image, not at the end of the disk, and a stale one there is what brings the old partitions back after a quick format.
/// </summary>
public static class DriveWiper
{
    private const int GptHeaderMinimum = 92;
    private const long MaxEntryArrayBytes = 1024 * 1024;

    /// <returns>The ranges that were cleared in addition to the start and the end of the disk.</returns>
    public static IReadOnlyList<ByteRange> Wipe(IBlockDevice device, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        // The old header has to be read before the start of the disk is cleared; it says where the backup is.
        var stale = FindBackupTable(device);
        cancellationToken.ThrowIfCancellationRequested();
        DiskWiper.WipeTables(device);

        // What the start and the end of the disk already cover is not cleared twice.
        var head = Math.Min(DiskWiper.DefaultHeadBytes, device.Length);
        var edge = RoundDown(device.Length, device.SectorSize) - Math.Min(DiskWiper.DefaultTailBytes, device.Length - head);
        var extra = new List<ByteRange>();
        foreach (var range in stale)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Math.Max(range.Start, head);
            var end = Math.Min(range.End, edge);
            if (end > start)
            {
                DiskWiper.ZeroRange(device, start, end - start);
                extra.Add(new ByteRange(start, end - start));
            }
        }

        return extra;
    }

    /// <summary>Where the backup entry array and header of the GPT in the first sectors lie, as far as they can be told from its primary header.</summary>
    internal static List<ByteRange> FindBackupTable(IBlockDevice device)
    {
        var result = new List<ByteRange>();
        var sector = device.SectorSize;
        if (device.Length < 3L * sector)
        {
            return result;
        }

        using var buffer = new AlignedBuffer(sector, device.BufferAlignment);
        var span = buffer.GetSpan();
        if (device.Read(sector, span) < GptHeaderMinimum || !span[..8].SequenceEqual("EFI PART"u8))
        {
            return result;
        }

        // The checksum is not checked: the disk is about to be erased, so a damaged header may still point at the old backup.
        var backupLba = BinaryPrimitives.ReadInt64LittleEndian(span[32..]);
        var entries = BinaryPrimitives.ReadUInt32LittleEndian(span[80..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(span[84..]);
        var total = device.Length / sector;
        var arrayBytes = (long)entries * entrySize;
        if (backupLba <= 1 || backupLba >= total || arrayBytes <= 0 || arrayBytes > MaxEntryArrayBytes)
        {
            return result;
        }

        var arraySectors = (arrayBytes + sector - 1) / sector;
        var first = Math.Max(2, backupLba - arraySectors);
        result.Add(new ByteRange(first * sector, (backupLba + 1 - first) * sector));
        return result;
    }

    private static long RoundDown(long value, int multiple) => value / multiple * multiple;
}
