// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Images.Apple;

internal sealed record GptEntry(int Index, Guid Type, long FirstLba, long LastLba, string Name);

internal sealed record GptTable(int SectorSize, IReadOnlyList<GptEntry> Entries);

/// <summary>
/// Reads the primary GPT for classification. It does not validate the CRCs: the point is to tell what the image
/// contains, and damaged tables are better reported as what they appear to hold.
/// </summary>
internal static class GptScanner
{
    private const int MaxEntries = 256;
    private const int MaxEntrySize = 1024;

    public static GptTable? TryRead(Stream stream)
    {
        foreach (var sectorSize in new[] { 512, 4096 })
        {
            var table = TryRead(stream, sectorSize);
            if (table is not null)
            {
                return table;
            }
        }

        return null;
    }

    private static GptTable? TryRead(Stream stream, int sectorSize)
    {
        Span<byte> header = stackalloc byte[92];
        if (StreamReading.ReadPadded(stream, sectorSize, header) < header.Length || !header.StartsWith("EFI PART"u8))
        {
            return null;
        }

        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header[72..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[84..]);
        if (count is 0 or > MaxEntries || entrySize is < 128 or > MaxEntrySize || entriesLba is < 2 or > int.MaxValue)
        {
            return null;
        }

        var entries = new List<GptEntry>();
        var buffer = new byte[(int)entrySize];
        for (var i = 0; i < count; i++)
        {
            var offset = ((long)entriesLba * sectorSize) + ((long)i * entrySize);
            if (StreamReading.ReadPadded(stream, offset, buffer) < 128)
            {
                break;
            }

            var type = new Guid(buffer.AsSpan(0, 16));
            if (type == Guid.Empty)
            {
                continue;
            }

            entries.Add(new GptEntry(
                i,
                type,
                Clamp(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32))),
                Clamp(BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(40))),
                ReadName(buffer.AsSpan(56, 72))));
        }

        return new GptTable(sectorSize, entries);
    }

    private static long Clamp(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var name = Encoding.Unicode.GetString(field);
        var end = name.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? name : name[..end];
    }
}
