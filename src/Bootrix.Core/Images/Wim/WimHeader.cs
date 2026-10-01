// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Images.Wim;

public enum WimCompression
{
    None,
    Xpress,
    Lzx,
    Lzms,
}

[Flags]
public enum WimFeatures : uint
{
    None = 0,
    Compressed = 0x2,
    ReadOnly = 0x4,
    Spanned = 0x8,
    ResourceOnly = 0x10,
    MetadataOnly = 0x20,
    WriteInProgress = 0x40,
    ReparsePointFix = 0x80,
    Xpress = 0x20000,
    Lzx = 0x40000,
    Lzms = 0x80000,
    Xpress2 = 0x200000,
}

/// <summary>
/// The 208-byte header at the start of every .wim, .esd and .swm file. Layout from Microsoft's
/// "Windows Imaging File Format" specification, cross-checked with wimlib's <c>wim.h</c>; all fields
/// are little endian.
/// </summary>
public sealed record WimHeader
{
    public const int Size = 208;

    /// <summary>wimlib and Microsoft write 0x10D00 for normal files; solid (ESD) archives use 0xE00.</summary>
    private const int SolidVersion = 0xE00;

    public required int Version { get; init; }

    public required WimFeatures Flags { get; init; }

    /// <summary>Compression chunk size in bytes; 0 for uncompressed files.</summary>
    public required int ChunkSize { get; init; }

    /// <summary>Identifies the WIM; all parts of a split WIM share it.</summary>
    public required Guid Id { get; init; }

    public required int PartNumber { get; init; }

    public required int TotalParts { get; init; }

    public required int ImageCount { get; init; }

    public required WimResource OffsetTable { get; init; }

    public required WimResource XmlData { get; init; }

    public required WimResource BootMetadata { get; init; }

    /// <summary>1-based index of the bootable image, 0 if there is none.</summary>
    public required int BootIndex { get; init; }

    public required WimResource Integrity { get; init; }

    public bool IsSplit => TotalParts > 1;

    public bool IsSolid => Version == SolidVersion;

    public WimCompression Compression
    {
        get
        {
            if (!Flags.HasFlag(WimFeatures.Compressed))
            {
                return WimCompression.None;
            }

            if (Flags.HasFlag(WimFeatures.Lzms))
            {
                return WimCompression.Lzms;
            }

            if (Flags.HasFlag(WimFeatures.Lzx))
            {
                return WimCompression.Lzx;
            }

            return WimCompression.Xpress;
        }
    }

    /// <summary>
    /// Smallest file length that contains every resource named in the header. The lookup table and
    /// XML data are written last, so a truncated download falls short of this value.
    /// </summary>
    public long ExpectedLength => new[] { OffsetTable, XmlData, BootMetadata, Integrity }.Max(resource => resource.End);

    public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith("MSWIM\0\0\0"u8);

    public static WimHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
        {
            throw ImageFailures.Unreadable("The file is shorter than a WIM header.");
        }

        if (!HasSignature(data))
        {
            throw ImageFailures.Unreadable("The WIM signature is missing.");
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        if (headerSize != Size)
        {
            throw ImageFailures.Unsupported($"WIM header of {headerSize} bytes");
        }

        return new WimHeader
        {
            Version = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x0C..]),
            Flags = (WimFeatures)BinaryPrimitives.ReadUInt32LittleEndian(data[0x10..]),
            ChunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x14..]),
            Id = new Guid(data.Slice(0x18, 16)),
            PartNumber = BinaryPrimitives.ReadUInt16LittleEndian(data[0x28..]),
            TotalParts = BinaryPrimitives.ReadUInt16LittleEndian(data[0x2A..]),
            ImageCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x2C..]),
            OffsetTable = WimResource.Parse(data[0x30..]),
            XmlData = WimResource.Parse(data[0x48..]),
            BootMetadata = WimResource.Parse(data[0x60..]),
            BootIndex = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[0x78..]),
            Integrity = WimResource.Parse(data[0x7C..]),
        };
    }
}
