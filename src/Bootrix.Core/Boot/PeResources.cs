// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Bootrix.Core.Boot;

/// <summary>
/// Minimal reader for the PE resource tree. The Win32 resource APIs are not available on Linux and the
/// tree of an untrusted file must be walked with explicit bounds, so only what the analysis needs is implemented.
/// </summary>
internal static class PeResources
{
    private const int DirectoryHeaderSize = 16;
    private const int EntrySize = 8;
    private const int MaxEntriesPerDirectory = 1024;
    private const uint HighBit = 0x8000_0000;
    private const uint RtRcData = 10;

    private readonly record struct Entry(uint NameOrId, uint Target)
    {
        public bool IsNamed => (NameOrId & HighBit) != 0;

        public bool IsDirectory => (Target & HighBit) != 0;

        public int NameOffset => (int)(NameOrId & ~HighBit);

        public int TargetOffset => (int)(Target & ~HighBit);
    }

    /// <summary>Data of the first language variant of the RCDATA resource called <paramref name="name"/>.</summary>
    public static ReadOnlyMemory<byte>? FindRcData(byte[] data, PEHeaders headers, string name)
    {
        var table = headers.PEHeader?.ResourceTableDirectory ?? default;
        if (table.Size == 0 || !headers.TryGetDirectoryOffset(table, out var root))
        {
            return null;
        }

        var types = ReadEntries(data, root, 0);
        var rcData = types.FirstOrDefault(e => !e.IsNamed && e.NameOrId == RtRcData);
        if (rcData == default || !rcData.IsDirectory)
        {
            return null;
        }

        foreach (var named in ReadEntries(data, root, rcData.TargetOffset))
        {
            if (!named.IsNamed || !named.IsDirectory || !NameEquals(data, root, named.NameOffset, name))
            {
                continue;
            }

            var language = ReadEntries(data, root, named.TargetOffset).FirstOrDefault(e => !e.IsDirectory);
            return language == default ? null : ReadData(data, headers, root, language.TargetOffset);
        }

        return null;
    }

    private static List<Entry> ReadEntries(byte[] data, int root, int directoryOffset)
    {
        var entries = new List<Entry>();
        var start = (long)root + directoryOffset;
        if (directoryOffset < 0 || start + DirectoryHeaderSize > data.Length)
        {
            return entries;
        }

        var span = data.AsSpan((int)start);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]) + BinaryPrimitives.ReadUInt16LittleEndian(span[14..]);
        count = Math.Min(count, MaxEntriesPerDirectory);
        for (var i = 0; i < count; i++)
        {
            var at = DirectoryHeaderSize + i * EntrySize;
            if (at + EntrySize > span.Length)
            {
                break;
            }

            entries.Add(new Entry(
                BinaryPrimitives.ReadUInt32LittleEndian(span[at..]),
                BinaryPrimitives.ReadUInt32LittleEndian(span[(at + 4)..])));
        }

        return entries;
    }

    private static bool NameEquals(byte[] data, int root, int nameOffset, string expected)
    {
        var start = (long)root + nameOffset;
        if (nameOffset < 0 || start + 2 > data.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)start));
        if (length != expected.Length || start + 2 + length * 2L > data.Length)
        {
            return false;
        }

        var actual = Encoding.Unicode.GetString(data, (int)start + 2, length * 2);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlyMemory<byte>? ReadData(byte[] data, PEHeaders headers, int root, int entryOffset)
    {
        var start = (long)root + entryOffset;
        if (entryOffset < 0 || start + 8 > data.Length)
        {
            return null;
        }

        var rva = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)start));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)start + 4));
        if (rva > int.MaxValue || size is 0 or > int.MaxValue
            || !headers.TryGetDirectoryOffset(new DirectoryEntry((int)rva, (int)size), out var offset)
            || (long)offset + size > data.Length)
        {
            return null;
        }

        return data.AsMemory(offset, (int)size);
    }
}
