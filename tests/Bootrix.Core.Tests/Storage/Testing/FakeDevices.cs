// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Storage.Testing;

/// <summary>A stick that claims more capacity than it has: addresses beyond the real end wrap around to the start.</summary>
internal sealed class WraparoundDevice(IBlockDevice real, long claimed) : IBlockDevice
{
    public string Name => "wraparound";

    public int SectorSize => real.SectorSize;

    public long Length => claimed;

    public int BufferAlignment => real.BufferAlignment;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        for (var done = 0; done < data.Length; done += SectorSize)
        {
            real.Write((offset + done) % real.Length, data.Slice(done, SectorSize));
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        for (var done = 0; done < buffer.Length; done += SectorSize)
        {
            real.Read((offset + done) % real.Length, buffer.Slice(done, SectorSize));
        }

        return buffer.Length;
    }

    public void Flush() => real.Flush();

    public void Dispose()
    {
    }
}

/// <summary>A stick that accepts writes beyond its real end but throws the data away.</summary>
internal sealed class SwallowingDevice(IBlockDevice real, long claimed) : IBlockDevice
{
    public string Name => "swallowing";

    public int SectorSize => real.SectorSize;

    public long Length => claimed;

    public int BufferAlignment => real.BufferAlignment;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset + data.Length <= real.Length)
        {
            real.Write(offset, data);
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset + buffer.Length <= real.Length)
        {
            return real.Read(offset, buffer);
        }

        buffer.Clear();
        return buffer.Length;
    }

    public void Flush() => real.Flush();

    public void Dispose()
    {
    }
}

/// <summary>Reports bad sectors: writes there are silently lost and reads there throw.</summary>
internal sealed class FlakyDevice(IBlockDevice inner, params long[] badSectors) : IBlockDevice
{
    private readonly HashSet<long> _bad = [.. badSectors];

    public string Name => "flaky";

    public int SectorSize => inner.SectorSize;

    public long Length => inner.Length;

    public int BufferAlignment => inner.BufferAlignment;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        for (var done = 0; done < data.Length; done += SectorSize)
        {
            if (!_bad.Contains((offset + done) / SectorSize))
            {
                inner.Write(offset + done, data.Slice(done, SectorSize));
            }
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        var read = inner.Read(offset, buffer);
        for (var done = 0; done < buffer.Length; done += SectorSize)
        {
            if (_bad.Contains((offset + done) / SectorSize))
            {
                buffer.Slice(done, SectorSize).Fill(0x13);
            }
        }

        return read;
    }

    public void Flush() => inner.Flush();

    public void Dispose()
    {
    }
}
