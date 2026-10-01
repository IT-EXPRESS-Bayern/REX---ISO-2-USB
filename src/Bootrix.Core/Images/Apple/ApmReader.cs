// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Images.Apple;

/// <summary>
/// Parser for the Apple Partition Map (big endian): the "ER" Driver Descriptor Record in block 0 followed by one
/// "PM" entry per partition. Real disks are sloppy, so counts and sizes are not trusted: the entries are located by
/// their signature at the usual block sizes and reading stops at the first block that is not an entry.
/// </summary>
public static class ApmReader
{
    private const ushort DriverDescriptorSignature = 0x4552;
    private const ushort EntrySignature = 0x504D;
    private const int EntrySize = 0x88;
    private const int MaxEntries = 256;

    private static readonly int[] CandidateBlockSizes = [512, 1024, 2048, 4096];

    /// <summary>Returns the partition map, or null if the stream does not start with one.</summary>
    public static ApmMap? TryRead(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> block0 = stackalloc byte[16];
        StreamReading.ReadPadded(stream, 0, block0);
        var hasDescriptor = BinaryPrimitives.ReadUInt16BigEndian(block0) == DriverDescriptorSignature;
        var declaredBlockSize = hasDescriptor ? BinaryPrimitives.ReadUInt16BigEndian(block0[2..]) : 0;
        var deviceBlocks = hasDescriptor ? BinaryPrimitives.ReadUInt32BigEndian(block0[4..]) : 0;

        var blockSize = FindEntryBlockSize(stream, declaredBlockSize);
        if (blockSize == 0)
        {
            return null;
        }

        Span<byte> entry = stackalloc byte[EntrySize];
        StreamReading.ReadPadded(stream, blockSize, entry);
        var mapEntries = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
        if (mapEntries is 0 or > MaxEntries)
        {
            return null;
        }

        var partitions = new List<ApmPartition>((int)mapEntries);
        for (var i = 1; i <= mapEntries; i++)
        {
            if (StreamReading.ReadPadded(stream, (long)i * blockSize, entry) < EntrySize
                || BinaryPrimitives.ReadUInt16BigEndian(entry) != EntrySignature)
            {
                break;
            }

            var start = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var count = BinaryPrimitives.ReadUInt32BigEndian(entry[0xC..]);
            partitions.Add(new ApmPartition(
                i - 1,
                ReadString(entry.Slice(0x10, 32)),
                ReadString(entry.Slice(0x30, 32)),
                start,
                count,
                (long)start * blockSize,
                (long)count * blockSize,
                BinaryPrimitives.ReadUInt32BigEndian(entry[0x58..])));
        }

        return partitions.Count == 0 ? null : new ApmMap(blockSize, hasDescriptor, deviceBlocks, partitions);
    }

    // The descriptor's block size is tried first, then the common sizes; CD images often declare 2048
    // in the descriptor while the entries are 512 bytes apart.
    private static int FindEntryBlockSize(Stream stream, int declared)
    {
        Span<byte> signature = stackalloc byte[2];
        var candidates = declared is >= 512 and <= 4096 && int.IsPow2(declared)
            ? [declared, .. CandidateBlockSizes]
            : CandidateBlockSizes;

        foreach (var size in candidates)
        {
            if (StreamReading.ReadPadded(stream, size, signature) == 2
                && BinaryPrimitives.ReadUInt16BigEndian(signature) == EntrySignature)
            {
                return size;
            }
        }

        return 0;
    }

    private static string ReadString(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? field : field[..end]);
    }
}
