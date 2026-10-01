// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

/// <summary>Presents a stream extended with zeros up to a larger length.</summary>
internal sealed class PaddedStream(Stream inner, long paddedLength) : ReadOnlyVolumeStream(paddedLength)
{
    private readonly long _innerLength = inner.Length;

    protected override int ReadCore(long position, Span<byte> destination)
    {
        if (position >= _innerLength)
        {
            destination.Clear();
            return destination.Length;
        }

        if (inner.Position != position)
        {
            inner.Position = position;
        }

        var wanted = (int)Math.Min(destination.Length, _innerLength - position);
        var read = inner.Read(destination[..wanted]);
        if (read == 0)
        {
            throw ImageErrors.Truncated(_innerLength, position);
        }

        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
