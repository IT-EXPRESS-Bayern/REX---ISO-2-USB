// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

internal static class StreamReading
{
    /// <summary>Reads as much as is available at the offset and zero-fills the remainder; returns the bytes actually read.</summary>
    public static int ReadPadded(Stream stream, long offset, Span<byte> buffer)
    {
        var total = 0;
        if (offset >= 0 && offset < stream.Length)
        {
            stream.Position = offset;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer[total..]);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }

        buffer[total..].Clear();
        return total;
    }
}
