// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Tooling;

/// <summary>
/// A seekable stream of a given length that stores only the pages that were written, so tests can
/// format and read back volumes of many gigabytes in memory. Untouched pages read as zeros.
/// </summary>
public sealed class SparseMemoryStream : Stream
{
    private const int PageSize = 64 * 1024;

    private readonly Dictionary<long, byte[]> _pages = [];
    private long _length;
    private long _position;

    public SparseMemoryStream(long length)
    {
        _length = length;
    }

    public long BytesWritten { get; private set; }

    public int PagesAllocated => _pages.Count;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => true;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = (int)Math.Min(count, Math.Max(0, _length - _position));
        var done = 0;
        while (done < total)
        {
            var page = _position / PageSize;
            var inPage = (int)(_position % PageSize);
            var chunk = Math.Min(total - done, PageSize - inPage);
            if (_pages.TryGetValue(page, out var data))
            {
                Buffer.BlockCopy(data, inPage, buffer, offset + done, chunk);
            }
            else
            {
                Array.Clear(buffer, offset + done, chunk);
            }

            done += chunk;
            _position += chunk;
        }

        return total;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var done = 0;
        while (done < count)
        {
            var page = _position / PageSize;
            var inPage = (int)(_position % PageSize);
            var chunk = Math.Min(count - done, PageSize - inPage);
            if (!_pages.TryGetValue(page, out var data))
            {
                data = new byte[PageSize];
                _pages[page] = data;
            }

            Buffer.BlockCopy(buffer, offset + done, data, inPage, chunk);
            done += chunk;
            _position += chunk;
        }

        BytesWritten += count;
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

    public override void SetLength(long value) => _length = value;

    public override void Flush()
    {
    }
}
