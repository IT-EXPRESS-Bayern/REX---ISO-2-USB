// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Core.Images.Apple;

/// <summary>
/// Positional reads on an image file. Implementations are safe to call from several threads,
/// which the read-ahead of the DMG reader relies on.
/// </summary>
internal abstract class RandomAccessSource : IDisposable
{
    public abstract long Length { get; }

    public abstract int ReadAt(long offset, Span<byte> buffer);

    public static RandomAccessSource OpenFile(string path)
    {
        try
        {
            return new FileSource(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ImageErrors.Unreadable(ex.Message, ex);
        }
    }

    public static RandomAccessSource FromStream(Stream stream, bool leaveOpen) => new StreamSource(stream, leaveOpen);

    public void ReadExactlyAt(long offset, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = ReadAt(offset + total, buffer[total..]);
            if (read <= 0)
            {
                throw ImageErrors.Truncated(offset + buffer.Length, offset + total);
            }

            total += read;
        }
    }

    /// <summary>Reads as much as is available at the offset and zero-fills the rest.</summary>
    public void ReadPadded(long offset, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length && offset + total < Length)
        {
            var read = ReadAt(offset + total, buffer[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        buffer[total..].Clear();
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    private sealed class FileSource(SafeFileHandle handle) : RandomAccessSource
    {
        private readonly long _length = RandomAccess.GetLength(handle);

        public override long Length => _length;

        public override int ReadAt(long offset, Span<byte> buffer) =>
            offset >= _length ? 0 : RandomAccess.Read(handle, buffer, offset);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                handle.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class StreamSource(Stream stream, bool leaveOpen) : RandomAccessSource
    {
        private readonly Lock _gate = new();

        public override long Length => stream.Length;

        public override int ReadAt(long offset, Span<byte> buffer)
        {
            lock (_gate)
            {
                if (offset >= stream.Length)
                {
                    return 0;
                }

                stream.Position = offset;
                return stream.Read(buffer);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
