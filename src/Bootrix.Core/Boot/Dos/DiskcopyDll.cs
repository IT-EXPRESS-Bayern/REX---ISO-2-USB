// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Boot.Dos;

/// <summary>
/// Finds the 1.44 MB boot diskette inside diskcopy.dll. Windows keeps the Windows ME startup disk there as a
/// custom resource of type BINFILE so that "diskcopy" can write it to a floppy. The tree is walked with
/// explicit bounds, because the file comes from the network.
/// </summary>
public static class DiskcopyDll
{
    public const int FloppyBytes = 1_474_560;

    private const string ResourceType = "BINFILE";
    private const int DirectoryHeaderSize = 16;
    private const int EntrySize = 8;
    private const int MaxEntriesPerDirectory = 256;
    private const uint HighBit = 0x8000_0000;

    /// <summary>The diskette image; throws <see cref="ErrorCode.MsDosImageInvalid"/> when the file has none or it is not a 1.44 MB FAT12 volume.</summary>
    public static ReadOnlyMemory<byte> FindFloppyImage(byte[] dll)
    {
        ArgumentNullException.ThrowIfNull(dll);

        PEHeaders headers;
        try
        {
            using var stream = new MemoryStream(dll, writable: false);
            headers = new PEHeaders(stream);
        }
        catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or IOException)
        {
            throw Invalid("not a PE file", ex);
        }

        var table = headers.PEHeader?.ResourceTableDirectory ?? default;
        if (table.Size == 0 || !headers.TryGetDirectoryOffset(table, out var root))
        {
            throw Invalid("no resource section");
        }

        foreach (var type in ReadEntries(dll, root, 0))
        {
            if (!type.IsDirectory || !IsNamed(dll, root, type, ResourceType))
            {
                continue;
            }

            foreach (var item in ReadEntries(dll, root, type.TargetOffset).Where(e => e.IsDirectory))
            {
                foreach (var language in ReadEntries(dll, root, item.TargetOffset).Where(e => !e.IsDirectory))
                {
                    var data = ReadData(dll, headers, root, language.TargetOffset);
                    if (data is { Length: FloppyBytes } image && IsFat12Floppy(image.Span))
                    {
                        return image;
                    }
                }
            }
        }

        throw Invalid("no 1.44 MB diskette image in the resources");
    }

    private static bool IsFat12Floppy(ReadOnlySpan<byte> image)
    {
        const int media144 = 0xF0;
        return image[510] == 0x55
            && image[511] == 0xAA
            && BinaryPrimitives.ReadUInt16LittleEndian(image[0x0B..]) == 512
            && BinaryPrimitives.ReadUInt16LittleEndian(image[0x13..]) == FloppyBytes / 512
            && image[0x15] == media144;
    }

    private readonly record struct Entry(uint NameOrId, uint Target)
    {
        public bool IsNamed => (NameOrId & HighBit) != 0;

        public bool IsDirectory => (Target & HighBit) != 0;

        public int NameOffset => (int)(NameOrId & ~HighBit);

        public int TargetOffset => (int)(Target & ~HighBit);
    }

    private static List<Entry> ReadEntries(byte[] data, int root, int directoryOffset)
    {
        var entries = new List<Entry>();
        var start = (long)root + directoryOffset;
        if (directoryOffset < 0 || start + DirectoryHeaderSize > data.Length)
        {
            return entries;
        }

        var directory = data.AsSpan((int)start);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(directory[12..]) + BinaryPrimitives.ReadUInt16LittleEndian(directory[14..]);
        for (var i = 0; i < Math.Min(count, MaxEntriesPerDirectory); i++)
        {
            var at = DirectoryHeaderSize + i * EntrySize;
            if (at + EntrySize > directory.Length)
            {
                break;
            }

            entries.Add(new Entry(
                BinaryPrimitives.ReadUInt32LittleEndian(directory[at..]),
                BinaryPrimitives.ReadUInt32LittleEndian(directory[(at + 4)..])));
        }

        return entries;
    }

    private static bool IsNamed(byte[] data, int root, Entry entry, string name)
    {
        var start = (long)root + entry.NameOffset;
        if (!entry.IsNamed || start + 2 > data.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)start));
        return length == name.Length
            && start + 2 + length * 2L <= data.Length
            && string.Equals(Encoding.Unicode.GetString(data, (int)start + 2, length * 2), name, StringComparison.OrdinalIgnoreCase);
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

    private static BootrixException Invalid(string detail, Exception? inner = null) =>
        new(ErrorCode.MsDosImageInvalid, detail, inner) { Arguments = [detail] };
}
