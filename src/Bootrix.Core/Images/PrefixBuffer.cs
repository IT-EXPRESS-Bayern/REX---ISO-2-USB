// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Images;

internal static class PrefixBuffer
{
    /// <summary>
    /// Reads up to <paramref name="limit"/> bytes from a forward-only stream into memory so that format readers,
    /// which need to seek, can look at the beginning of a compressed image.
    /// </summary>
    public static MemoryStream Read(Stream source, int limit) => Read(source, limit, out _);

    /// <summary>
    /// Like <see cref="Read(Stream,int)"/>, but a decoding failure ends the read instead of discarding what was
    /// decoded so far; the failure is returned so that the damaged start of an image can still be analysed.
    /// </summary>
    public static MemoryStream Read(Stream source, int limit, out BootrixException? failure)
    {
        failure = null;
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        try
        {
            while (buffer.Length < limit)
            {
                var read = source.Read(chunk, 0, (int)Math.Min(chunk.Length, limit - buffer.Length));
                if (read == 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (BootrixException ex) when (ex.Code is ErrorCode.ImageUnreadable or ErrorCode.ImageTruncated)
        {
            failure = ex;
        }

        buffer.Position = 0;
        return buffer;
    }
}
