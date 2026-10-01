// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Threading.Channels;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Storage;

/// <summary>
/// Copies one image to one or more block devices. The source is read and hashed once, the data is
/// handed to a writer thread per target, and afterwards every target is read back and compared
/// chunk by chunk against the hashes taken while the source was read.
/// </summary>
public sealed class RawImageWriter(ILogger<RawImageWriter>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<RawImageWriter>.Instance;

    public async Task<RawWriteReport> WriteAsync(
        Stream source,
        long? sourceLength,
        IReadOnlyList<IBlockDevice> targets,
        RawWriteOptions? options = null,
        IProgress<RawWriteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new RawWriteOptions();
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one target is required.", nameof(targets));
        }

        var alignment = targets.Max(t => t.BufferAlignment);
        var sectorLcm = targets.Select(t => t.SectorSize).Aggregate(Lcm);
        var chunkSize = RoundDown(Math.Max(options.ChunkSize, sectorLcm), sectorLcm);
        var headSize = options.HoldBackBytes > 0 ? RoundUp(options.HoldBackBytes, sectorLcm) : 0;

        var states = targets.Select(t => new TargetState(t, options.BufferCount)).ToList();
        if (sourceLength is { } expected)
        {
            foreach (var state in states.Where(s => RoundUp(expected, s.Device.SectorSize) > s.Device.Length))
            {
                state.Fail(new BootrixException(ErrorCode.DeviceTooSmall, state.Device.Name)
                {
                    Arguments = [FormatSize(expected), FormatSize(state.Device.Length)],
                });
            }
        }

        using var pool = new BufferPool(options.BufferCount + 1, chunkSize, alignment);
        using var headBuffer = headSize > 0 ? new AlignedBuffer(headSize, alignment) : null;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var records = new List<ChunkRecord>();
        var writers = states.Select(s => Task.Run(() => RunWriter(s), CancellationToken.None)).ToList();

        long imageBytes = 0;
        try
        {
            var headLength = 0;
            if (headBuffer is not null)
            {
                headLength = await ReadFullAsync(source, headBuffer.Memory[..headSize], cancellationToken).ConfigureAwait(false);
                sha.AppendData(headBuffer.GetSpan()[..headLength]);
                imageBytes += headLength;
            }

            var offset = (long)headSize;
            var finished = headBuffer is not null && headLength < headSize;
            while (!finished)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var buffer = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
                var read = await ReadFullAsync(source, buffer.Memory[..chunkSize], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    pool.Return(buffer);
                    break;
                }

                finished = read < chunkSize;
                sha.AppendData(buffer.GetSpan()[..read]);
                imageBytes += read;

                var padded = RoundUp(read, sectorLcm);
                buffer.GetSpan()[read..padded].Clear();
                records.Add(new ChunkRecord(offset, read, ChunkHash.Compute(buffer.GetSpan()[..read])));
                Dispatch(states, new Chunk(buffer, offset, padded, read, pool));

                offset += padded;
                progress?.Report(new RawWriteProgress(RawWritePhase.Writing, imageBytes, Math.Max(sourceLength ?? 0, imageBytes)));
            }

            if (headBuffer is not null && headLength > 0)
            {
                var padded = RoundUp(headLength, sectorLcm);
                headBuffer.GetSpan()[headLength..padded].Clear();
                records.Add(new ChunkRecord(0, headLength, ChunkHash.Compute(headBuffer.GetSpan()[..headLength])));
                var head = new Chunk(headBuffer, 0, padded, headLength, null);
                Dispatch(states, head);
                await head.WaitUntilReleasedAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var state in states)
            {
                state.Queue.Writer.TryComplete();
            }

            await Task.WhenAll(writers).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        _log.LogInformation("Wrote {Bytes} bytes to {Count} target(s), SHA-256 {Hash}", imageBytes, states.Count, hash);

        if (options.Verify)
        {
            records.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            foreach (var state in states.Where(s => s.Error is null))
            {
                Verify(state, records, imageBytes, chunkSize, alignment, progress, cancellationToken);
            }
        }

        return new RawWriteReport(
            imageBytes,
            hash,
            [.. states.Select(s => new RawTargetResult(s.Device, s.Error is null, s.Error, s.BytesWritten, s.Verified))]);
    }

    private static void Dispatch(List<TargetState> states, Chunk chunk)
    {
        var live = states.Where(s => s.Error is null).ToList();
        chunk.Expect(live.Count);
        foreach (var state in live)
        {
            if (!state.Queue.Writer.TryWrite(chunk))
            {
                chunk.Release();
            }
        }
    }

    private void RunWriter(TargetState state)
    {
        while (state.Queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (state.Queue.Reader.TryRead(out var chunk))
            {
                try
                {
                    if (state.Error is null)
                    {
                        state.Device.Write(chunk.Offset, chunk.Buffer.GetSpan()[..chunk.PaddedLength]);
                        state.BytesWritten += chunk.PaddedLength;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Writing to {Target} failed at offset {Offset}", state.Device.Name, chunk.Offset);
                    state.Fail(ex);
                }
                finally
                {
                    chunk.Release();
                }
            }
        }

        if (state.Error is not null)
        {
            return;
        }

        try
        {
            state.Device.Flush();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Flushing {Target} failed", state.Device.Name);
            state.Fail(ex);
        }
    }

    private void Verify(
        TargetState state,
        List<ChunkRecord> records,
        long imageBytes,
        int chunkSize,
        int alignment,
        IProgress<RawWriteProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            state.Verified = true;
            return;
        }

        var sector = state.Device.SectorSize;
        using var buffer = new AlignedBuffer(Math.Max(chunkSize, records.Max(r => RoundUp(r.Length, sector))), alignment);
        long done = 0;

        try
        {
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var span = buffer.GetSpan()[..RoundUp(record.Length, sector)];
                var read = state.Device.Read(record.Offset, span);
                if (read < record.Length || ChunkHash.Compute(span[..record.Length]) != record.Hash)
                {
                    throw new BootrixException(ErrorCode.VerifyMismatch, $"{state.Device.Name} @ {record.Offset}")
                    {
                        Arguments = [record.Offset],
                    };
                }

                done += record.Length;
                progress?.Report(new RawWriteProgress(RawWritePhase.Verifying, done, imageBytes));
            }

            state.Verified = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Verification of {Target} failed", state.Device.Name);
            state.Fail(ex);
        }
    }

    private static async Task<int> ReadFullAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static int RoundUp(long value, int multiple) => (int)((value + multiple - 1) / multiple * multiple);

    private static int RoundDown(int value, int multiple) => value / multiple * multiple;

    private static int Lcm(int a, int b) => a / Gcd(a, b) * b;

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    private static string FormatSize(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.#} GB"
        : $"{bytes / (double)(1L << 20):0.#} MB";

    private sealed class TargetState(IBlockDevice device, int capacity)
    {
        public IBlockDevice Device { get; } = device;

        public Channel<Chunk> Queue { get; } = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(capacity + 2)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        public Exception? Error { get; private set; }

        public long BytesWritten { get; set; }

        public bool Verified { get; set; }

        public void Fail(Exception ex) => Error ??= ex;
    }
}
