// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Udif;

/// <summary>
/// Hands out decoded chunks: from the cache, from a decode that was started ahead of time, or by decoding on the
/// calling thread. When reads walk through the image in order, the next chunks are decoded on pool threads while the
/// caller is still busy with the current one. Not thread-safe; only the decode callback runs on other threads.
/// </summary>
internal sealed class ChunkPipeline : IDisposable
{
    private readonly UdifChunk[] _chunks;
    private readonly int _readAhead;
    private readonly long _budget;
    private readonly Func<int, CancellationToken, byte[]> _decode;
    private readonly ChunkCache _cache;
    private readonly Dictionary<int, PendingChunk> _pending = [];
    private readonly List<Task> _abandoned = [];
    private readonly CancellationTokenSource _shutdown = new();
    private long _pendingBytes;
    private int _lastIndex = -1;

    public ChunkPipeline(UdifChunk[] chunks, DmgReaderOptions options, Func<int, CancellationToken, byte[]> decode)
    {
        _chunks = chunks;
        _readAhead = options.ReadAhead;
        _budget = options.CacheBytes;
        _decode = decode;
        _cache = new ChunkCache(options.CacheBytes);
    }

    public byte[] Get(int index)
    {
        if (!_cache.TryGet(index, out var data))
        {
            data = _pending.Remove(index, out var pending) ? Complete(pending) : _decode(index, CancellationToken.None);
            _cache.Add(index, data);
        }

        if (index != _lastIndex)
        {
            if (_readAhead > 0)
            {
                UpdateReadAhead(index);
            }

            _lastIndex = index;
        }

        return data;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            Task.WaitAll([.. _pending.Values.Select(p => p.Task), .. _abandoned]);
        }
        catch (AggregateException)
        {
            // Read-ahead that failed or was cancelled is irrelevant once the reader is closed.
        }

        _pending.Clear();
        _shutdown.Dispose();
    }

    private byte[] Complete(PendingChunk pending)
    {
        _pendingBytes -= pending.Bytes;
        return pending.Task.GetAwaiter().GetResult();
    }

    private void UpdateReadAhead(int current)
    {
        // Reading in order means this chunk is the one after the previous (or the same again).
        var sequential = current == _lastIndex || NextDecoded(_lastIndex) == current;
        var wanted = sequential ? Following(current, _readAhead) : [];

        foreach (var stale in _pending.Keys.Where(index => !wanted.Contains(index)).ToList())
        {
            _pendingBytes -= _pending[stale].Bytes;
            _abandoned.Add(_pending[stale].Task);
            _pending.Remove(stale);
        }

        ForgetFinished();

        foreach (var index in wanted)
        {
            if (_cache.Contains(index) || _pending.ContainsKey(index))
            {
                continue;
            }

            var bytes = _chunks[index].SectorCount * DmgReader.SectorSize;
            if (_cache.Bytes + _pendingBytes + bytes > _budget)
            {
                return;
            }

            var token = _shutdown.Token;
            var chunk = index;
            _pending[index] = new PendingChunk(Task.Run(() => _decode(chunk, token), token), bytes);
            _pendingBytes += bytes;
        }
    }

    private void ForgetFinished()
    {
        _abandoned.RemoveAll(task =>
        {
            if (!task.IsCompleted)
            {
                return false;
            }

            // Reading the exception marks it as observed.
            _ = task.Exception;
            return true;
        });
    }

    // Chunks that need decoding; raw, zero and ignore chunks are served without the pipeline.
    // After -1 this is the first such chunk, so a read from the start of the image counts as sequential.
    private int NextDecoded(int after)
    {
        for (var i = after + 1; i < _chunks.Length; i++)
        {
            if (IsDecoded(_chunks[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private List<int> Following(int current, int count)
    {
        var result = new List<int>(count);
        var next = current;
        while (result.Count < count && (next = NextDecoded(next)) >= 0)
        {
            result.Add(next);
        }

        return result;
    }

    private static bool IsDecoded(in UdifChunk chunk) => chunk.HasData && chunk.Type != UdifChunkType.Raw;

    private readonly record struct PendingChunk(Task<byte[]> Task, long Bytes);
}
