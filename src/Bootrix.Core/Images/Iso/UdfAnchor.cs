// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Images.Iso;

/// <summary>The part of a UDF volume that tells how large the volume is supposed to be.</summary>
/// <param name="BlockSize">Logical block size (2048 on optical media, the sector size on disks).</param>
/// <param name="PartitionStartBlock">First block of the (first) partition.</param>
/// <param name="PartitionBlocks">Length of the partition in blocks.</param>
public sealed record UdfVolume(int BlockSize, long PartitionStartBlock, long PartitionBlocks)
{
    public long ExpectedBytes => (PartitionStartBlock + PartitionBlocks) * BlockSize;
}

/// <summary>
/// Locates the Anchor Volume Descriptor Pointer at logical sector 256 (ECMA-167 3/8.4.2) and follows it to the
/// partition descriptor of the main volume descriptor sequence. The anchor is the only fixed address, so it
/// is tried with each plausible block size.
/// </summary>
public static class UdfAnchor
{
    private const int AnchorSector = 256;
    private const ushort AnchorTag = 2;
    private const ushort PartitionTag = 5;
    private const ushort LogicalVolumeTag = 6;
    private const ushort TerminatorTag = 8;
    private const int MaxSequenceBytes = 64 * 1024;

    private static readonly int[] BlockSizes = [2048, 512, 4096, 1024];

    public static UdfVolume? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        foreach (var blockSize in BlockSizes)
        {
            var anchorOffset = (long)AnchorSector * blockSize;
            if (anchorOffset + 512 > stream.Length)
            {
                continue;
            }

            var anchor = new byte[512];
            stream.Position = anchorOffset;
            stream.ReadExactly(anchor);
            if (!IsTag(anchor, AnchorTag))
            {
                continue;
            }

            var sequenceBytes = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(16)), MaxSequenceBytes);
            var sequenceStart = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(20)) * (long)blockSize;
            var volume = ReadSequence(stream, sequenceStart, sequenceBytes, blockSize);
            if (volume is not null)
            {
                return volume;
            }
        }

        return null;
    }

    private static UdfVolume? ReadSequence(Stream stream, long offset, int length, int blockSize)
    {
        if (length <= 0 || offset + length > stream.Length)
        {
            return null;
        }

        var data = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(data);

        var logicalBlockSize = blockSize;
        long start = 0;
        long blocks = 0;
        var foundPartition = false;

        for (var position = 0; position + 512 <= data.Length; position += blockSize)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            if (tag == TerminatorTag)
            {
                break;
            }

            if (!IsTag(data.AsSpan(position, 512), tag))
            {
                continue;
            }

            if (tag == LogicalVolumeTag)
            {
                logicalBlockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 212));
            }
            else if (tag == PartitionTag && !foundPartition)
            {
                start = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 188));
                blocks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 192));
                foundPartition = true;
            }
        }

        return foundPartition ? new UdfVolume(logicalBlockSize is 512 or 1024 or 2048 or 4096 ? logicalBlockSize : blockSize, start, blocks) : null;
    }

    /// <summary>
    /// Descriptor tag (ECMA-167 3/7.2): the checksum is the sum of the other 15 tag bytes modulo 256, which makes
    /// accidental matches of the tag identifier unlikely.
    /// </summary>
    private static bool IsTag(ReadOnlySpan<byte> data, ushort expectedId)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(data) != expectedId)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 16; i++)
        {
            if (i != 4)
            {
                sum += data[i];
            }
        }

        return (byte)sum == data[4];
    }
}
