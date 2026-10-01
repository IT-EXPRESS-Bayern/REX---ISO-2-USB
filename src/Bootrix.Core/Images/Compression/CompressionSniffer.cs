// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Recognises compressed containers from their first bytes. The file extension is never consulted:
/// downloads are routinely renamed.
/// </summary>
public static class CompressionSniffer
{
    /// <summary>Number of leading bytes <see cref="Detect"/> needs for a reliable answer.</summary>
    public const int HeaderLength = 16;

    private static ReadOnlySpan<byte> XzMagic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    private static ReadOnlySpan<byte> Bzip2BlockMagic => [0x31, 0x41, 0x59, 0x26, 0x53, 0x59];

    public static CompressionFormat Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length < 3)
        {
            return CompressionFormat.None;
        }

        if (head[0] == 0x1F && head[1] == 0x8B && head[2] == 0x08)
        {
            return CompressionFormat.GZip;
        }

        if (head[0] == 0x1F && head[1] == 0x9D && (head[2] & 0x60) == 0 && (head[2] & 0x1F) is >= 9 and <= 16)
        {
            return CompressionFormat.Compress;
        }

        if (head.StartsWith("BZh"u8) && head.Length >= 10 && head[3] is >= (byte)'1' and <= (byte)'9'
            && head[4..10].SequenceEqual(Bzip2BlockMagic))
        {
            return CompressionFormat.BZip2;
        }

        if (head.StartsWith(XzMagic))
        {
            return CompressionFormat.Xz;
        }

        if (IsZstdMagic(head))
        {
            return CompressionFormat.Zstd;
        }

        if (head.StartsWith("PK\x03\x04"u8))
        {
            return CompressionFormat.Zip;
        }

        return LooksLikeLzmaAlone(head) ? CompressionFormat.Lzma : CompressionFormat.None;
    }

    private static bool IsZstdMagic(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        // Skippable frames (0x184D2A50..5F) may precede the first real frame.
        return magic == 0xFD2FB528 || (magic & 0xFFFFFFF0) == 0x184D2A50;
    }

    /// <summary>
    /// The 13-byte LZMA-alone header has no signature, so its fields are checked for plausibility and the
    /// first byte of the range coder stream, which the encoder always writes as zero.
    /// </summary>
    private static bool LooksLikeLzmaAlone(ReadOnlySpan<byte> head)
    {
        if (head.Length < 14)
        {
            return false;
        }

        // lc + 9 * (lp + 5 * pb) must stay below 9 * 5 * 5.
        if (head[0] >= 9 * 5 * 5)
        {
            return false;
        }

        if (!IsPlausibleDictionarySize(BinaryPrimitives.ReadUInt32LittleEndian(head[1..])))
        {
            return false;
        }

        var size = BinaryPrimitives.ReadUInt64LittleEndian(head[5..]);
        return (size == ulong.MaxValue || size < (1UL << 48)) && head[13] == 0;
    }

    /// <summary>The reference encoder only produces 2^n and 3*2^(n-1) dictionaries.</summary>
    private static bool IsPlausibleDictionarySize(uint size)
    {
        if (size < 4096)
        {
            return false;
        }

        return size == uint.MaxValue || uint.IsPow2(size) || (size % 3 == 0 && uint.IsPow2(size / 3));
    }
}
