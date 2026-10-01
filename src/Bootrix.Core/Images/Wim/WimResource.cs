// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Images.Wim;

/// <summary>
/// A resource header as stored in the WIM header: 7 bytes of stored size, one flags byte, the file
/// offset and the original (uncompressed) size.
/// </summary>
public readonly record struct WimResource(long Offset, long StoredSize, long OriginalSize, byte Flags)
{
    public const int DiskSize = 24;

    private const byte CompressedFlag = 0x04;

    public bool IsCompressed => (Flags & CompressedFlag) != 0;

    public bool IsEmpty => StoredSize == 0 && Offset == 0;

    /// <summary>First byte after the resource, 0 for an absent one.</summary>
    public long End => IsEmpty ? 0 : Offset + StoredSize;

    public static WimResource Parse(ReadOnlySpan<byte> data)
    {
        var sizeAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(data);
        return new WimResource(
            Offset: (long)BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
            StoredSize: (long)(sizeAndFlags & 0x00FF_FFFF_FFFF_FFFF),
            OriginalSize: (long)BinaryPrimitives.ReadUInt64LittleEndian(data[16..]),
            Flags: (byte)(sizeAndFlags >> 56));
    }
}
