// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Core.Net;

internal static class FileHasher
{
    private const int ChunkSize = 1024 * 1024;

    /// <summary>
    /// Computes every requested digest in one pass. The next chunk is read while the current one is hashed,
    /// so a fast disk and a fast CPU overlap instead of taking turns.
    /// </summary>
    public static async Task<IReadOnlyList<FileHash>> ComputeAsync(
        SafeFileHandle file,
        long length,
        IEnumerable<HashKind> kinds,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        var hashers = kinds.Distinct().Select(k => (Kind: k, Hash: IncrementalHash.CreateHash(k.AlgorithmName()))).ToList();
        var current = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var ahead = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            long position = 0;
            var read = await RandomAccess.ReadAsync(file, current.AsMemory(0, (int)Math.Min(ChunkSize, length)), 0, cancellationToken).ConfigureAwait(false);

            while (read > 0)
            {
                var next = position + read;
                var pending = next < length
                    ? RandomAccess.ReadAsync(file, ahead.AsMemory(0, (int)Math.Min(ChunkSize, length - next)), next, cancellationToken)
                    : default;

                foreach (var (_, hash) in hashers)
                {
                    hash.AppendData(current, 0, read);
                }

                position = next;
                progress?.Invoke(position);

                read = await pending.ConfigureAwait(false);
                (current, ahead) = (ahead, current);
            }

            return [.. hashers.Select(h => new FileHash(h.Kind, Convert.ToHexStringLower(h.Hash.GetHashAndReset())))];
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(current);
            ArrayPool<byte>.Shared.Return(ahead);
            foreach (var (_, hash) in hashers)
            {
                hash.Dispose();
            }
        }
    }

    public static async Task<bool> MatchesAsync(SafeFileHandle file, long offset, long length, FileHash expected, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(expected.Kind.AlgorithmName());
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            var position = offset;
            var end = offset + length;
            while (position < end)
            {
                var read = await RandomAccess.ReadAsync(file, buffer.AsMemory(0, (int)Math.Min(ChunkSize, end - position)), position, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return false;
                }

                hash.AppendData(buffer, 0, read);
                position += read;
            }

            return expected.Matches(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
