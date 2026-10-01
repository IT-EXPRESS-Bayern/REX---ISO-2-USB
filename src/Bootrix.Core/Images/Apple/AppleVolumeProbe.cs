// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Images.Apple;

/// <summary>What a file system signature check found at one position.</summary>
/// <param name="BlessedFolder">Catalog ID of the folder that holds the boot files; non-zero on a startable volume.</param>
/// <param name="Size">Volume size in bytes according to its header, 0 if unknown.</param>
/// <param name="Name">Volume name where the header carries one (classic HFS only).</param>
internal readonly record struct VolumeProbeResult(AppleFileSystem FileSystem, uint BlessedFolder, long Size, string? Name)
{
    public static VolumeProbeResult None => new(AppleFileSystem.Unknown, 0, 0, null);

    public bool Found => FileSystem != AppleFileSystem.Unknown;
}

/// <summary>
/// Recognises HFS, HFS+/HFSX and APFS volumes by their headers (Technical Note TN1150 and the Apple File System
/// Reference): the HFS and HFS+ headers sit 1024 bytes into the volume, the APFS container superblock in block 0.
/// </summary>
internal static class AppleVolumeProbe
{
    private const int HeaderOffset = 1024;
    private const int ProbeLength = 1024 + 128;
    private const ushort HfsSignature = 0x4244;
    private const ushort HfsPlusSignature = 0x482B;
    private const ushort HfsXSignature = 0x4858;

    public static VolumeProbeResult Probe(Stream stream, long offset)
    {
        Span<byte> buffer = stackalloc byte[ProbeLength];
        StreamReading.ReadPadded(stream, offset, buffer);

        // The container superblock starts with a 32-byte object header, then the little-endian magic "NXSB".
        if (buffer.Slice(32, 4).SequenceEqual("NXSB"u8))
        {
            return ProbeApfs(buffer);
        }

        var header = buffer[HeaderOffset..];
        var signature = BinaryPrimitives.ReadUInt16BigEndian(header);
        return signature switch
        {
            HfsPlusSignature or HfsXSignature => ProbeHfsPlus(header),
            HfsSignature => ProbeHfs(stream, offset, header),
            _ => VolumeProbeResult.None,
        };
    }

    private static VolumeProbeResult ProbeApfs(ReadOnlySpan<byte> block)
    {
        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(block[36..]);
        if (blockSize is < 4096 or > 65536 || !uint.IsPow2(blockSize))
        {
            return VolumeProbeResult.None;
        }

        var blocks = BinaryPrimitives.ReadUInt64LittleEndian(block[40..]);
        var size = blocks <= (ulong)(long.MaxValue / blockSize) ? (long)blocks * blockSize : 0;
        return new VolumeProbeResult(AppleFileSystem.Apfs, 0, size, null);
    }

    private static VolumeProbeResult ProbeHfsPlus(ReadOnlySpan<byte> header)
    {
        var signature = BinaryPrimitives.ReadUInt16BigEndian(header);
        var version = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        var blockSize = BinaryPrimitives.ReadUInt32BigEndian(header[40..]);
        var plus = signature == HfsPlusSignature;
        if (version != (plus ? 4 : 5) || blockSize < 512 || !uint.IsPow2(blockSize))
        {
            return VolumeProbeResult.None;
        }

        var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(header[44..]);
        var blessed = BinaryPrimitives.ReadUInt32BigEndian(header[80..]);
        return new VolumeProbeResult(plus ? AppleFileSystem.HfsPlus : AppleFileSystem.HfsX, blessed, (long)totalBlocks * blockSize, null);
    }

    private static VolumeProbeResult ProbeHfs(Stream stream, long offset, ReadOnlySpan<byte> mdb)
    {
        var allocationBlockSize = BinaryPrimitives.ReadUInt32BigEndian(mdb[20..]);
        var allocationBlocks = BinaryPrimitives.ReadUInt16BigEndian(mdb[18..]);

        // An HFS wrapper around HFS+ announces the embedded volume in drEmbedSigWord.
        if (BinaryPrimitives.ReadUInt16BigEndian(mdb[124..]) == HfsPlusSignature && allocationBlockSize >= 512)
        {
            var firstAllocationBlock = BinaryPrimitives.ReadUInt16BigEndian(mdb[28..]);
            var embeddedStart = BinaryPrimitives.ReadUInt16BigEndian(mdb[126..]);
            var embedded = offset + ((long)firstAllocationBlock * 512) + ((long)embeddedStart * allocationBlockSize);

            Span<byte> inner = stackalloc byte[ProbeLength];
            StreamReading.ReadPadded(stream, embedded, inner);
            var plus = ProbeHfsPlus(inner[HeaderOffset..]);
            if (plus.Found)
            {
                return plus;
            }
        }

        if (allocationBlockSize == 0 || allocationBlockSize % 512 != 0 || allocationBlocks == 0)
        {
            return VolumeProbeResult.None;
        }

        var nameLength = Math.Min((int)mdb[36], 27);
        var name = Encoding.Latin1.GetString(mdb.Slice(37, nameLength));
        var blessed = BinaryPrimitives.ReadUInt32BigEndian(mdb[92..]);
        return new VolumeProbeResult(AppleFileSystem.Hfs, blessed, (long)allocationBlocks * allocationBlockSize, name);
    }
}
