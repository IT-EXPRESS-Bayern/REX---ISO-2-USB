// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// Pulls the 'blkx' resources out of a classic Mac resource fork. Images without an XML
/// property list (very old UDIF files) keep their block tables there.
/// </summary>
internal static class ResourceForkReader
{
    private const int HeaderSize = 16;
    private const int MapHeaderSize = 28;
    private const int TypeEntrySize = 8;
    private const int ReferenceSize = 12;
    private const int MaxResources = 1 << 16;

    public static List<BlkxResource> ReadBlkx(ReadOnlySpan<byte> fork)
    {
        if (fork.Length < HeaderSize)
        {
            throw ImageErrors.Corrupt("resource fork is too short");
        }

        var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(fork);
        var mapOffset = BinaryPrimitives.ReadUInt32BigEndian(fork[4..]);
        var mapLength = BinaryPrimitives.ReadUInt32BigEndian(fork[12..]);
        if (mapLength < MapHeaderSize + 2 || mapOffset > fork.Length || mapLength > fork.Length - mapOffset || dataOffset > fork.Length)
        {
            throw ImageErrors.Corrupt("resource map lies outside the resource fork");
        }

        var map = fork.Slice((int)mapOffset, (int)mapLength);
        var typeListOffset = BinaryPrimitives.ReadUInt16BigEndian(map[24..]);
        var nameListOffset = BinaryPrimitives.ReadUInt16BigEndian(map[26..]);
        if (typeListOffset + 2 > map.Length)
        {
            throw ImageErrors.Corrupt("resource type list lies outside the resource map");
        }

        var typeCount = BinaryPrimitives.ReadUInt16BigEndian(map[typeListOffset..]) + 1;
        var result = new List<BlkxResource>();

        // Resources that overlap would let a small fork expand into a huge allocation.
        long remainingBytes = fork.Length;
        for (var i = 0; i < typeCount; i++)
        {
            var typeEntryOffset = typeListOffset + 2 + (i * TypeEntrySize);
            if (typeEntryOffset + TypeEntrySize > map.Length)
            {
                throw ImageErrors.Corrupt("resource type list is truncated");
            }

            var typeEntry = map.Slice(typeEntryOffset, TypeEntrySize);
            if (!typeEntry.StartsWith("blkx"u8))
            {
                continue;
            }

            var referenceCount = BinaryPrimitives.ReadUInt16BigEndian(typeEntry[4..]) + 1;
            var referenceListOffset = typeListOffset + BinaryPrimitives.ReadUInt16BigEndian(typeEntry[6..]);
            for (var j = 0; j < referenceCount; j++)
            {
                if (result.Count >= MaxResources)
                {
                    throw ImageErrors.Corrupt("too many resources");
                }

                var resource = ReadResource(fork, map, dataOffset, referenceListOffset + (j * ReferenceSize), nameListOffset);
                remainingBytes -= resource.Data.Length;
                if (remainingBytes < 0)
                {
                    throw ImageErrors.Corrupt("resource data overlaps");
                }

                result.Add(resource);
            }
        }

        return result;
    }

    private static BlkxResource ReadResource(ReadOnlySpan<byte> fork, ReadOnlySpan<byte> map, uint dataStart, int referenceOffset, int nameListOffset)
    {
        if (referenceOffset + ReferenceSize > map.Length)
        {
            throw ImageErrors.Corrupt("resource reference list is truncated");
        }

        var reference = map.Slice(referenceOffset, ReferenceSize);
        var id = BinaryPrimitives.ReadInt16BigEndian(reference);
        var nameOffset = BinaryPrimitives.ReadUInt16BigEndian(reference[2..]);
        var attributes = reference[4];
        var relativeData = (reference[5] << 16) | (reference[6] << 8) | reference[7];

        var position = (long)dataStart + relativeData;
        if (position + 4 > fork.Length)
        {
            throw ImageErrors.Corrupt("resource data lies outside the resource fork");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(fork[(int)position..]);
        if (length > fork.Length - position - 4)
        {
            throw ImageErrors.Corrupt("resource data lies outside the resource fork");
        }

        var data = fork.Slice((int)position + 4, (int)length).ToArray();
        return new BlkxResource(id, ReadName(map, nameListOffset, nameOffset), attributes, data);
    }

    private static string ReadName(ReadOnlySpan<byte> map, int nameListOffset, ushort nameOffset)
    {
        if (nameOffset == 0xFFFF)
        {
            return string.Empty;
        }

        var position = nameListOffset + nameOffset;
        if (position >= map.Length)
        {
            return string.Empty;
        }

        var length = Math.Min(map[position], map.Length - position - 1);
        return Encoding.Latin1.GetString(map.Slice(position + 1, length));
    }
}
