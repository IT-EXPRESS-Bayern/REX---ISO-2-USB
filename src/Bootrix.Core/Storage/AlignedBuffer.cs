// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Runtime.InteropServices;

namespace Bootrix.Core.Storage;

/// <summary>
/// Page-aligned native buffer. Unbuffered disk I/O requires the buffer address, the offset and the
/// length to be multiples of the sector size, which a normal managed array cannot guarantee.
/// </summary>
public sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private void* _pointer;
    private readonly int _length;

    public AlignedBuffer(int length, int alignment = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _length = length;
        _pointer = NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
        NativeMemory.Clear(_pointer, (nuint)length);
    }

    public int Length => _length;

    public override Span<byte> GetSpan() =>
        _pointer is null ? throw new ObjectDisposedException(nameof(AlignedBuffer)) : new Span<byte>(_pointer, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_pointer is null, this);
        return new MemoryHandle((byte*)_pointer + elementIndex);
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (_pointer is not null)
        {
            NativeMemory.AlignedFree(_pointer);
            _pointer = null;
        }
    }
}
