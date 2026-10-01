// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// Copies a stream with two buffers: while one block is written, the next is already being read.
/// An ISO on a hard disk and a stick on USB are slow in different moments, and this keeps both busy.
/// </summary>
internal sealed class StreamPump(int bufferBytes)
{
    private readonly byte[][] _buffers = [new byte[bufferBytes], new byte[bufferBytes]];

    public int BufferBytes => bufferBytes;

    /// <summary>Copies everything <paramref name="input"/> has left; returns the number of bytes and, when asked for, their SHA-256.</summary>
    public async Task<(long Bytes, byte[]? Hash)> CopyAsync(
        Stream input, Stream output, bool hash, Action<int> advance, CancellationToken cancellationToken)
    {
        using var sha = hash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        long total = 0;
        var current = 0;
        var pending = input.ReadAsync(_buffers[current], cancellationToken);
        try
        {
            while (true)
            {
                var completed = pending;
                pending = default;
                var count = await completed.ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var filled = _buffers[current];
                current ^= 1;
                pending = input.ReadAsync(_buffers[current], cancellationToken);

                sha?.AppendData(filled.AsSpan(0, count));
                await output.WriteAsync(filled.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                total += count;
                advance(count);
            }
        }
        catch
        {
            await Drain(pending).ConfigureAwait(false);
            throw;
        }

        return (total, sha?.GetHashAndReset());
    }

    /// <summary>Reads a stream to its end and returns its SHA-256; used for what already sits on the target.</summary>
    public async Task<byte[]> HashAsync(Stream input, Action<int> advance, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = _buffers[0];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer.AsSpan(0, count));
            advance(count);
        }

        return sha.GetHashAndReset();
    }

    private static async Task Drain(ValueTask<int> read)
    {
        try
        {
            await read.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The copy has failed already; the outstanding read must only be finished before the buffers are reused.
        }
    }
}
