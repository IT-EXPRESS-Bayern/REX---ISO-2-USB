// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Optical;

/// <summary>SHA-256 of an image plus a hash per chunk, so a failed comparison can say where the disc differs.</summary>
public sealed record ImageDigest(string Sha256, long Length, int ChunkSize, IReadOnlyList<ulong> ChunkHashes)
{
    public const int DefaultChunkSize = 512 * SectorMath.SectorSize;

    public static async Task<ImageDigest> ComputeAsync(
        Stream stream,
        IProgress<long>? progress = null,
        int chunkSize = DefaultChunkSize,
        CancellationToken cancellationToken = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunks = new List<ulong>();
        var buffer = new byte[chunkSize];
        long total = 0;
        while (true)
        {
            var read = await ReadFullAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sha.AppendData(buffer, 0, read);
            chunks.Add(ChunkHash.Compute(buffer.AsSpan(0, read)));
            total += read;
            progress?.Report(total);
            if (read < buffer.Length)
            {
                break;
            }
        }

        return new ImageDigest(Convert.ToHexStringLower(sha.GetHashAndReset()), total, chunkSize, chunks);
    }

    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
