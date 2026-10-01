// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Writing.Verify;

public enum VerifyMode
{
    /// <summary>Raw when the start of the medium is the start of the image, the files of the image otherwise.</summary>
    Auto,

    /// <summary>The medium holds the image byte for byte.</summary>
    Raw,

    /// <summary>The medium holds the files of the image in a file system of its own.</summary>
    Files,
}

/// <summary>
/// Outcome of a byte-for-byte comparison. Differences are counted per block of 4096 bytes, and the offset of the
/// first differing byte of the first blocks is kept, which is what a person needs to tell a dying stick from a bad write.
/// </summary>
public sealed record RawVerifyReport(long BytesCompared, long DifferingBlocks, IReadOnlyList<long> FirstOffsets)
{
    /// <summary>Bytes of the image that were not looked at because a block map marks them as unused.</summary>
    public long BytesSkipped { get; init; }

    public bool Matches => DifferingBlocks == 0;

    /// <summary>The error for a medium that does not match; null when it does.</summary>
    public BootrixException? ToException(string deviceName) => Matches
        ? null
        : new BootrixException(
            ErrorCode.VerifyMismatch,
            $"{deviceName}: {DifferingBlocks} blocks differ, first differences at offset {string.Join(", ", FirstOffsets.Select(offset => "0x" + offset.ToString("X", System.Globalization.CultureInfo.InvariantCulture)))}")
        {
            Arguments = [FirstOffsets.Count > 0 ? FirstOffsets[0] : 0],
        };
}

/// <summary>Compares a decoded image with what a block device holds, up to the length of the image.</summary>
public static class RawVerifier
{
    private const int BlockSize = 4096;
    private const int MaxReported = 16;

    /// <param name="image">The decoded image, read forward once.</param>
    /// <param name="imageLength">The length when the container knows it; a longer image than the device is refused up front.</param>
    /// <param name="sparse">Compare only these ranges; the rest of the medium may hold anything.</param>
    public static async Task<RawVerifyReport> CompareAsync(
        Stream image,
        long? imageLength,
        IBlockDevice device,
        SparseWriteMap? sparse = null,
        IProgress<RawWriteProgress>? progress = null,
        int chunkSize = 8 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(device);

        var sector = device.SectorSize;
        var deviceBytes = device.Length / sector * sector;
        if (imageLength is { } expected && expected > deviceBytes)
        {
            throw new BootrixException(ErrorCode.DeviceTooSmall, device.Name)
            {
                Arguments = [SizeText.Format(expected), SizeText.Format(deviceBytes)],
            };
        }

        var chunk = Math.Max(chunkSize, sector) / sector * sector;
        var imageBuffer = new byte[chunk];
        using var deviceBuffer = new AlignedBuffer(chunk, device.BufferAlignment);
        var reported = new List<long>();
        long offset = 0;
        long compared = 0;
        long differing = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The medium is read while the next piece of the image is decoded.
            var wanted = (int)Math.Min(chunk, Math.Max(0, deviceBytes - offset));
            var chunkOffset = offset;
            var deviceRead = Task.Run(() => wanted == 0 ? 0 : device.Read(chunkOffset, deviceBuffer.GetSpan()[..wanted]), CancellationToken.None);
            var read = await ReadFullAsync(image, imageBuffer, cancellationToken).ConfigureAwait(false);
            var available = await deviceRead.ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var disk = deviceBuffer.GetSpan();
            foreach (var piece in Pieces(sparse, offset, read, sector))
            {
                var from = (int)(piece.Start - offset);
                var length = (int)Math.Min(piece.Length, read - from);
                if (length <= 0)
                {
                    continue;
                }

                compared += length;
                differing += CompareRange(imageBuffer.AsSpan(from, length), disk, from, available, offset + from, reported);
            }

            offset += read;
            progress?.Report(new RawWriteProgress(RawWritePhase.Verifying, offset, Math.Max(imageLength ?? 0, offset)));
            if (read < chunk)
            {
                break;
            }
        }

        return new RawVerifyReport(compared, differing, reported) { BytesSkipped = offset - compared };
    }

    private static List<ByteRange> Pieces(SparseWriteMap? sparse, long offset, int read, int sector) =>
        sparse is null || sparse.FillGaps ? [new ByteRange(offset, read)] : sparse.Select(offset, (read + sector - 1) / sector * sector, sector);

    /// <returns>The number of 4096-byte blocks of <paramref name="expected"/> that differ from the medium's bytes at <paramref name="at"/>.</returns>
    private static long CompareRange(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> medium, int at, int available, long absoluteOffset, List<long> reported)
    {
        long differing = 0;
        for (var done = 0; done < expected.Length; done += BlockSize)
        {
            var length = Math.Min(BlockSize, expected.Length - done);
            var start = at + done;
            var present = Math.Clamp(available - start, 0, length);
            var equal = present == length && expected.Slice(done, length).SequenceEqual(medium.Slice(start, length));
            if (equal)
            {
                continue;
            }

            differing++;
            if (reported.Count < MaxReported)
            {
                reported.Add(absoluteOffset + done + expected.Slice(done, present).CommonPrefixLength(medium.Slice(start, present)));
            }
        }

        return differing;
    }

    private static async Task<int> ReadFullAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
