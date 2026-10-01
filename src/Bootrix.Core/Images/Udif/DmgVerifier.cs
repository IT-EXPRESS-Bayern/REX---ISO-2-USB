// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Buffers.Binary;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// Recomputes the three UDIF checksums. The data fork checksum covers the stored bytes, a partition
/// checksum the decoded partition, and the master checksum the partition checksums themselves.
/// </summary>
internal static class DmgVerifier
{
    private const int BufferSize = 1024 * 1024;

    public static DmgChecksumReport Verify(
        DmgLayout layout,
        RandomAccessSource source,
        Func<int, byte[]> loadChunk,
        CancellationToken cancellationToken)
    {
        var dataFork = VerifyDataFork(layout.Trailer, source, cancellationToken);

        var chunkIndexes = new List<int>[layout.Partitions.Length];
        for (var i = 0; i < chunkIndexes.Length; i++)
        {
            chunkIndexes[i] = [];
        }

        for (var i = 0; i < layout.Chunks.Length; i++)
        {
            chunkIndexes[layout.Chunks[i].Partition].Add(i);
        }

        var partitions = new List<DmgPartitionChecksum>(layout.Partitions.Length);
        var master = new UdifCrc32();
        Span<byte> word = stackalloc byte[4];

        for (var p = 0; p < layout.Partitions.Length; p++)
        {
            var partition = layout.Partitions[p];
            partitions.Add(new DmgPartitionChecksum(partition, VerifyPartition(layout, p, chunkIndexes[p], source, loadChunk, cancellationToken)));

            if (partition.Checksum.IsCrc32)
            {
                BinaryPrimitives.WriteUInt32BigEndian(word, partition.Checksum.Crc32);
                master.Append(word);
            }
        }

        return new DmgChecksumReport(dataFork, partitions, Compare(layout.Trailer.MasterChecksum, master.Value, computed: true));
    }

    private static DmgChecksumResult VerifyDataFork(UdifTrailer trailer, RandomAccessSource source, CancellationToken cancellationToken)
    {
        if (!trailer.DataChecksum.IsCrc32)
        {
            return Compare(trailer.DataChecksum, 0, computed: false);
        }

        if (trailer.DataForkLength > (ulong)source.Length)
        {
            throw ImageErrors.Truncated((long)Math.Min(trailer.DataForkLength, long.MaxValue), source.Length);
        }

        var crc = new UdifCrc32();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var position = trailer.BaseOffset + (long)trailer.DataForkOffset;
            var remaining = (long)trailer.DataForkLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, BufferSize);
                source.ReadExactlyAt(position, buffer.AsSpan(0, count));
                crc.Append(buffer.AsSpan(0, count));
                position += count;
                remaining -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Compare(trailer.DataChecksum, crc.Value, computed: true);
    }

    private static DmgChecksumResult VerifyPartition(
        DmgLayout layout,
        int partition,
        List<int> chunkIndexes,
        RandomAccessSource source,
        Func<int, byte[]> loadChunk,
        CancellationToken cancellationToken)
    {
        var expected = layout.Partitions[partition].Checksum;
        if (!expected.IsCrc32)
        {
            return Compare(expected, 0, computed: false);
        }

        // Free-space (ignore) chunks are left out: the checksum covers the data stream that was written,
        // and 7-Zip verifies real hdiutil images the same way.
        var crc = new UdifCrc32();
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            foreach (var index in chunkIndexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = layout.Chunks[index];
                var length = chunk.SectorCount * DmgReader.SectorSize;
                switch (chunk.Type)
                {
                    case UdifChunkType.ZeroFill:
                        crc.AppendZeros(length);
                        break;
                    case UdifChunkType.Ignore:
                        break;
                    case UdifChunkType.Raw:
                        for (long done = 0; done < length; done += BufferSize)
                        {
                            var count = (int)Math.Min(length - done, BufferSize);
                            source.ReadExactlyAt(chunk.FileOffset + done, buffer.AsSpan(0, count));
                            crc.Append(buffer.AsSpan(0, count));
                        }

                        break;
                    default:
                        crc.Append(loadChunk(index));
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Compare(expected, crc.Value, computed: true);
    }

    private static DmgChecksumResult Compare(UdifChecksum stored, uint actual, bool computed)
    {
        if (!stored.IsPresent)
        {
            return new DmgChecksumResult(DmgChecksumStatus.NotPresent, 0, 0);
        }

        if (!stored.IsCrc32 || !computed)
        {
            return new DmgChecksumResult(DmgChecksumStatus.Unsupported, stored.Crc32, 0);
        }

        return new DmgChecksumResult(stored.Crc32 == actual ? DmgChecksumStatus.Valid : DmgChecksumStatus.Mismatch, stored.Crc32, actual);
    }
}
