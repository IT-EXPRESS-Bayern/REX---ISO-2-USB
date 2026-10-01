// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>An in-memory stream that only stores the pages that were written, so 64 GiB images cost a few hundred kilobytes.</summary>
internal sealed class SparseMemoryStream : Stream
{
    private const int PageSize = 64 * 1024;

    private readonly Dictionary<long, byte[]> _pages = [];
    private long _length;
    private long _position;

    public SparseMemoryStream(long length = 0)
    {
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public int StoredPages => _pages.Count;

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, Math.Max(0, _length - _position));
        var done = 0;
        while (done < count)
        {
            var page = (_position + done) / PageSize;
            var inPage = (int)((_position + done) % PageSize);
            var chunk = Math.Min(count - done, PageSize - inPage);
            if (_pages.TryGetValue(page, out var data))
            {
                data.AsSpan(inPage, chunk).CopyTo(buffer[done..]);
            }
            else
            {
                buffer.Slice(done, chunk).Clear();
            }

            done += chunk;
        }

        _position += count;
        return count;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var page = (_position + done) / PageSize;
            var inPage = (int)((_position + done) % PageSize);
            var chunk = Math.Min(buffer.Length - done, PageSize - inPage);
            var source = buffer.Slice(done, chunk);
            if (!_pages.TryGetValue(page, out var data))
            {
                if (!source.ContainsAnyExcept((byte)0))
                {
                    done += chunk;
                    continue;
                }

                data = _pages[page] = new byte[PageSize];
            }

            source.CopyTo(data.AsSpan(inPage));
            done += chunk;
        }

        _position += buffer.Length;
        _length = Math.Max(_length, _position);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void SetLength(long value)
    {
        _length = value;
        foreach (var page in _pages.Keys.Where(page => page * PageSize >= value).ToList())
        {
            _pages.Remove(page);
        }
    }

    public byte[] ReadAt(long offset, int count)
    {
        var buffer = new byte[count];
        _position = offset;
        ReadExactly(buffer);
        return buffer;
    }

    public byte[] ToArray() => ReadAt(0, checked((int)_length));
}
