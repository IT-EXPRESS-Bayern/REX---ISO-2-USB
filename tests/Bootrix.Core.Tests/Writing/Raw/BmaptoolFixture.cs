// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Bootrix.Core.Tests.Writing.Raw;

/// <summary>
/// A block map written by bmaptool 3.7 (<c>bmaptool create</c>) for an 8 MiB image whose only data lies in blocks 0,
/// 256-260 and 1464-1466; the image itself is regenerated from a hash keystream, so the checksums in the map are real.
/// </summary>
internal static class BmaptoolFixture
{
    public const int ImageSize = 8 * 1024 * 1024;

    public static readonly byte[] BlockMapBytes = Encoding.UTF8.GetBytes(BlockMapText + "\n");

    private const string BlockMapText = """
<?xml version="1.0" ?>
<!-- This file contains the block map for an image file, which is basically
     a list of useful (mapped) block numbers in the image file. In other words,
     it lists only those blocks which contain data (boot sector, partition
     table, file-system metadata, files, directories, extents, etc). These
     blocks have to be copied to the target device. The other blocks do not
     contain any useful data and do not have to be copied to the target
     device.

     The block map is an optimization which allows to copy or flash the image
     to the image quicker than copying of flashing the entire image. This is
     because with bmap less data is copied: <MappedBlocksCount> blocks instead
     of <BlocksCount> blocks.

     Besides the machine-readable data, this file contains useful commentaries
     which contain human-readable information like image size, percentage of
     mapped data, etc.

     The 'version' attribute is the block map file format version in the
     'major.minor' format. The version major number is increased whenever an
     incompatible block map format change is made. The minor number changes
     in case of minor backward-compatible changes. -->

<bmap version="2.0">
    <!-- Image size in bytes: 8.0 MiB -->
    <ImageSize> 8388608 </ImageSize>

    <!-- Size of a block in bytes -->
    <BlockSize> 4096 </BlockSize>

    <!-- Count of blocks in the image file -->
    <BlocksCount> 2048 </BlocksCount>

    <!-- Count of mapped blocks: 36.0 KiB or 0.4%    -->
    <MappedBlocksCount> 9    </MappedBlocksCount>

    <!-- Type of checksum used in this file -->
    <ChecksumType> sha256 </ChecksumType>

    <!-- The checksum of this bmap file. When it is calculated, the value of
         the checksum has to be zero (all ASCII "0" symbols).  -->
    <BmapFileChecksum> b30de67aa84528e73753d80ec4e39081f56ac2a1ca7aac9462a9213c55d27c3c </BmapFileChecksum>

    <!-- The block map which consists of elements which may either be a
         range of blocks or a single block. The 'chksum' attribute
         (if present) is the checksum of this blocks range. -->
    <BlockMap>
        <Range chksum="38056246e3ab24867c0fb7cb6c271f7993d0c07a5ce32fc25990f975b0755ff9"> 0 </Range>
        <Range chksum="c97a12fd3fe050a326426d025d1c552b600eaa6d6365d8d5ca8308a44b50949e"> 256-260 </Range>
        <Range chksum="c21f6f062a63eefdd8f7d90938e587cbac5c91e9e44da4f042d71f268900c866"> 1464-1466 </Range>
    </BlockMap>
</bmap>
""";

    /// <summary>The image as <c>mkfixture.py</c> wrote it: SHA-256 keystream in the mapped blocks, zeros elsewhere.</summary>
    public static byte[] Image()
    {
        var image = new byte[ImageSize];
        foreach (var (first, last) in new[] { (0, 0), (256, 260), (1464, 1466) })
        {
            var start = first * 4096;
            Keystream(start, (last - first + 1) * 4096, image.AsSpan(start));
        }

        return image;
    }

    private static void Keystream(long offset, int length, Span<byte> destination)
    {
        var counter = offset / 32;
        var skip = (int)(offset % 32);
        var produced = 0;
        Span<byte> input = stackalloc byte[12 + 8];
        "bootrix-bmap"u8.CopyTo(input);
        Span<byte> digest = stackalloc byte[32];
        while (produced < length)
        {
            BitConverter.TryWriteBytes(input[12..], counter++);
            SHA256.HashData(input, digest);
            var take = Math.Min(32 - skip, length - produced);
            digest.Slice(skip, take).CopyTo(destination[produced..]);
            produced += take;
            skip = 0;
        }
    }
}
