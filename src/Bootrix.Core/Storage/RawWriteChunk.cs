// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;

namespace Bootrix.Core.Storage;

/// <summary>Hands out the fixed set of aligned buffers; the reader waits here when all are still being written.</summary>
internal sealed class BufferPool : IDisposable
{
    private readonly Channel<AlignedBuffer> _free = Channel.CreateUnbounded<AlignedBuffer>();
    private readonly List<AlignedBuffer> _all = [];

    public BufferPool(int count, int size, int alignment)
    {
        for (var i = 0; i < count; i++)
        {
            var buffer = new AlignedBuffer(size, alignment);
            _all.Add(buffer);
            _free.Writer.TryWrite(buffer);
        }
    }

    public ValueTask<AlignedBuffer> RentAsync(CancellationToken cancellationToken) => _free.Reader.ReadAsync(cancellationToken);

    public void Return(AlignedBuffer buffer) => _free.Writer.TryWrite(buffer);

    public void Dispose()
    {
        foreach (var buffer in _all)
        {
            ((IDisposable)buffer).Dispose();
        }
    }
}

/// <summary>
/// One block of data on its way to several targets. The buffer goes back to the pool when the
/// last target has finished with it.
/// </summary>
internal sealed class Chunk(AlignedBuffer buffer, long offset, int paddedLength, int dataLength, BufferPool? pool)
{
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;

    public AlignedBuffer Buffer { get; } = buffer;

    public long Offset { get; } = offset;

    public int PaddedLength { get; } = paddedLength;

    public int DataLength { get; } = dataLength;

    public void Expect(int consumers)
    {
        _pending = consumers;
        if (consumers == 0)
        {
            Finish();
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _pending) == 0)
        {
            Finish();
        }
    }

    public Task WaitUntilReleasedAsync() => _released.Task;

    private void Finish()
    {
        pool?.Return(Buffer);
        _released.TrySetResult();
    }
}
