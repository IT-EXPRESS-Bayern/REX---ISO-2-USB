// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

/// <summary>Writes small cabinets with exactly the folders, blocks and flags a test asks for.</summary>
internal sealed class CabinetBuilder
{
    public const ushort Stored = 0;
    public const ushort MsZip = 1;
    public const ushort Quantum = 2;

    private readonly List<(ushort Type, List<(byte[] Payload, int Uncompressed)> Blocks)> _folders = [];
    private readonly List<(string Name, int Folder, uint Offset, uint Length, bool Utf8)> _files = [];

    public bool Checksums { get; init; } = true;

    public ushort Flags { get; init; }

    /// <summary>Sizes of the reserved areas; any non-zero value switches the header flag on. Signed cabinets reserve 20 bytes in the header.</summary>
    public int HeaderReserve { get; init; }

    public int FolderReserve { get; init; }

    public int DataReserve { get; init; }

    public static ushort Lzx(int windowBits) => (ushort)(3 | (windowBits << 8));

    public CabinetBuilder Folder(ushort compression, params (byte[] Payload, int Uncompressed)[] blocks)
    {
        _folders.Add((compression, [.. blocks]));
        return this;
    }

    public CabinetBuilder File(string name, int folder, uint offset, uint length, bool utf8 = false)
    {
        _files.Add((name, folder, offset, length, utf8));
        return this;
    }

    public byte[] Build()
    {
        var reserved = HeaderReserve > 0 || FolderReserve > 0 || DataReserve > 0;
        var folderTableStart = 36 + (reserved ? 4 + HeaderReserve : 0);
        var folderTableEnd = folderTableStart + (8 + FolderReserve) * _folders.Count;
        var fileTable = _files.Sum(f => 16 + (f.Utf8 ? Encoding.UTF8 : Encoding.Latin1).GetByteCount(f.Name) + 1);
        var dataStart = folderTableEnd + fileTable;

        var output = new MemoryStream();
        var header = new byte[36];
        "MSCF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)folderTableEnd);
        header[24] = 3;
        header[25] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)_folders.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)_files.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), (ushort)(Flags | (reserved ? 0x0004 : 0)));
        output.Write(header);

        if (reserved)
        {
            var reserve = new byte[4 + HeaderReserve];
            BinaryPrimitives.WriteUInt16LittleEndian(reserve, (ushort)HeaderReserve);
            reserve[2] = (byte)FolderReserve;
            reserve[3] = (byte)DataReserve;
            Array.Fill(reserve, (byte)0xAB, 4, HeaderReserve);
            output.Write(reserve);
        }

        var offset = dataStart;
        foreach (var (type, blocks) in _folders)
        {
            var entry = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)offset);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), (ushort)blocks.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), type);
            output.Write(entry);
            output.Write(new byte[FolderReserve]);
            offset += blocks.Sum(b => 8 + DataReserve + b.Payload.Length);
        }

        foreach (var (name, folder, fileOffset, length, utf8) in _files)
        {
            var entry = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), fileOffset);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)folder);
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(14), (ushort)(utf8 ? 0x80 : 0x20));
            output.Write(entry);
            output.Write((utf8 ? Encoding.UTF8 : Encoding.Latin1).GetBytes(name));
            output.WriteByte(0);
        }

        foreach (var (_, blocks) in _folders)
        {
            foreach (var (payload, uncompressed) in blocks)
            {
                var block = new byte[8];
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), (ushort)payload.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(6), (ushort)uncompressed);
                if (Checksums)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(block, Checksum(payload, Checksum(block.AsSpan(4), 0)));
                }

                output.Write(block);
                output.Write(new byte[DataReserve]);
                output.Write(payload);
            }
        }

        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length);
        return bytes;
    }

    // The format's checksum is not a plain word XOR: a trailing partial word is read with the first byte highest.
    private static uint Checksum(ReadOnlySpan<byte> data, uint seed)
    {
        var sum = seed;
        var index = 0;
        for (; index + 4 <= data.Length; index += 4)
        {
            sum ^= BinaryPrimitives.ReadUInt32LittleEndian(data[index..]);
        }

        uint tail = 0;
        for (; index < data.Length; index++)
        {
            tail = (tail << 8) | data[index];
        }

        return sum ^ tail;
    }
}
