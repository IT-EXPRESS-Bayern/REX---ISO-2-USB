// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Bootrix.Core.Catalog.Microsoft;

internal sealed record CabinetEntry(string Name, int Folder, long Offset, long Length);

/// <summary>
/// Reads a single, self-contained cabinet (the format of Microsoft's <c>products.cab</c>) completely in memory.
/// Stored, MSZIP and LZX folders are understood; sets that span several cabinets and Quantum are rejected.
/// Corrupt data raises <see cref="InvalidDataException"/>, unsupported features <see cref="NotSupportedException"/>.
/// </summary>
internal sealed class CabinetArchive
{
    private const int HeaderSize = 36;
    private const ushort HasPreviousCabinet = 0x0001;
    private const ushort HasNextCabinet = 0x0002;
    private const ushort HasReserve = 0x0004;
    private const ushort ContinuedFlagsStart = 0xFFFD;
    private const int MsZipHistory = 32768;

    private readonly byte[] _data;
    private readonly FolderInfo[] _folders;
    private readonly Dictionary<int, byte[]> _decoded = [];

    private CabinetArchive(byte[] data, FolderInfo[] folders, IReadOnlyList<CabinetEntry> entries, int dataReserve, long maxFolderSize)
    {
        _data = data;
        _folders = folders;
        Entries = entries;
        DataReserve = dataReserve;
        MaxFolderSize = maxFolderSize;
    }

    public IReadOnlyList<CabinetEntry> Entries { get; }

    private int DataReserve { get; }

    private long MaxFolderSize { get; }

    private enum Compression
    {
        None = 0,
        MsZip = 1,
        Quantum = 2,
        Lzx = 3,
    }

    /// <param name="maxFolderSize">Upper bound for the unpacked size of one folder; protects against decompression bombs.</param>
    public static CabinetArchive Parse(byte[] data, long maxFolderSize = 64L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length < HeaderSize || !data.AsSpan(0, 4).SequenceEqual("MSCF"u8))
        {
            throw new InvalidDataException("Not a cabinet file.");
        }

        var header = data.AsSpan(0, HeaderSize);
        var filesOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var folderCount = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var fileCount = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);

        if ((flags & (HasPreviousCabinet | HasNextCabinet)) != 0)
        {
            throw new NotSupportedException("Cabinet sets spanning several files are not supported.");
        }

        var position = HeaderSize;
        int folderReserve = 0, dataReserve = 0;

        if ((flags & HasReserve) != 0)
        {
            Require(data, position, 4);
            var headerReserve = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            folderReserve = data[position + 2];
            dataReserve = data[position + 3];
            position += 4 + headerReserve;
        }

        var folders = new FolderInfo[folderCount];
        for (var i = 0; i < folderCount; i++)
        {
            Require(data, position, 8);
            var folder = data.AsSpan(position, 8);
            folders[i] = new FolderInfo(
                BinaryPrimitives.ReadUInt32LittleEndian(folder),
                BinaryPrimitives.ReadUInt16LittleEndian(folder[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(folder[6..]));
            position += 8 + folderReserve;
        }

        var entries = ReadEntries(data, filesOffset, fileCount, folderCount);
        return new CabinetArchive(data, folders, entries, dataReserve, maxFolderSize);
    }

    public byte[] Extract(CabinetEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_decoded.TryGetValue(entry.Folder, out var folder))
        {
            folder = DecodeFolder(entry.Folder);
            _decoded[entry.Folder] = folder;
        }

        if (entry.Offset + entry.Length > folder.Length)
        {
            throw new InvalidDataException($"File '{entry.Name}' reaches past the end of its folder.");
        }

        return folder.AsSpan((int)entry.Offset, (int)entry.Length).ToArray();
    }

    private static List<CabinetEntry> ReadEntries(byte[] data, long offset, int count, int folderCount)
    {
        var entries = new List<CabinetEntry>(count);
        var position = offset;

        for (var i = 0; i < count; i++)
        {
            Require(data, position, 17);
            var file = data.AsSpan((int)position, 16);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(file);
            var start = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
            var folder = BinaryPrimitives.ReadUInt16LittleEndian(file[8..]);
            var attributes = BinaryPrimitives.ReadUInt16LittleEndian(file[14..]);

            if (folder >= ContinuedFlagsStart)
            {
                throw new NotSupportedException("Files continued from or into another cabinet are not supported.");
            }

            if (folder >= folderCount)
            {
                throw new InvalidDataException($"File refers to folder {folder} of {folderCount}.");
            }

            var nameStart = (int)position + 16;
            var nameEnd = Array.IndexOf(data, (byte)0, nameStart);
            if (nameEnd < 0)
            {
                throw new InvalidDataException("File name is not terminated.");
            }

            // Attribute bit 7 marks UTF-8 names; everything else is the OEM code page, of which ASCII is all that matters here.
            var encoding = (attributes & 0x80) != 0 ? Encoding.UTF8 : Encoding.Latin1;
            entries.Add(new CabinetEntry(encoding.GetString(data, nameStart, nameEnd - nameStart), folder, start, size));
            position = nameEnd + 1;
        }

        return entries;
    }

    private static void Require(byte[] data, long position, long length)
    {
        if (position < 0 || position + length > data.Length)
        {
            throw new InvalidDataException("Cabinet is truncated.");
        }
    }

    private byte[] DecodeFolder(int index)
    {
        var folder = _folders[index];
        var compression = (Compression)(folder.CompressionType & 0x0F);
        var windowBits = (folder.CompressionType >> 8) & 0x1F;

        if (compression == Compression.Quantum)
        {
            throw new NotSupportedException("Quantum-compressed cabinets are not supported.");
        }

        if (compression is not (Compression.None or Compression.MsZip or Compression.Lzx))
        {
            throw new InvalidDataException($"Unknown cabinet compression type {folder.CompressionType:X4}.");
        }

        var lzx = compression == Compression.Lzx ? new LzxDecoder(windowBits) : null;
        var blocks = ReadBlocks(folder);
        var total = blocks.Sum(b => (long)b.Uncompressed);

        if (total > MaxFolderSize)
        {
            throw new InvalidDataException($"Folder unpacks to {total} bytes, more than the limit of {MaxFolderSize}.");
        }

        var output = new byte[total];
        var position = 0;

        foreach (var block in blocks)
        {
            var payload = _data.AsSpan(block.Offset, block.Compressed);
            var target = output.AsSpan(position, block.Uncompressed);

            switch (compression)
            {
                case Compression.None:
                    if (block.Compressed != block.Uncompressed)
                    {
                        throw new InvalidDataException("Stored block changes size.");
                    }

                    payload.CopyTo(target);
                    break;
                case Compression.MsZip:
                    InflateMsZip(payload, output, position, block.Uncompressed);
                    break;
                default:
                    lzx!.DecodeFrame(payload, output, position, block.Uncompressed);
                    break;
            }

            position += block.Uncompressed;
        }

        return output;
    }

    private List<DataBlock> ReadBlocks(FolderInfo folder)
    {
        var blocks = new List<DataBlock>(folder.BlockCount);
        long position = folder.DataOffset;

        for (var i = 0; i < folder.BlockCount; i++)
        {
            Require(_data, position, 8 + DataReserve);
            var header = _data.AsSpan((int)position, 8);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(header);
            int compressed = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            int uncompressed = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);

            var payloadStart = position + 8 + DataReserve;
            Require(_data, payloadStart, compressed);

            // Zero means "not computed".
            if (checksum != 0 && Checksum(_data.AsSpan((int)payloadStart, compressed), Checksum(header[4..], 0)) != checksum)
            {
                throw new InvalidDataException($"Checksum of data block {i} does not match.");
            }

            blocks.Add(new DataBlock((int)payloadStart, compressed, uncompressed));
            position = payloadStart + compressed;
        }

        return blocks;
    }

    /// <summary>XOR over little-endian words. A trailing partial word is read the other way round, first byte highest.</summary>
    private static uint Checksum(ReadOnlySpan<byte> data, uint seed)
    {
        var sum = seed;
        var words = data.Length / 4;

        for (var i = 0; i < words; i++)
        {
            sum ^= BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
        }

        uint tail = 0;
        var rest = data[(words * 4)..];
        for (var i = 0; i < rest.Length; i++)
        {
            tail = (tail << 8) | rest[i];
        }

        return sum ^ tail;
    }

    /// <summary>
    /// MSZIP blocks are deflate streams after a "CK" tag, and later blocks may refer back into the 32 KiB before
    /// them. Framework inflate has no preset dictionary, so the previous output is fed in first as a stored block.
    /// </summary>
    private static void InflateMsZip(ReadOnlySpan<byte> payload, byte[] output, int position, int length)
    {
        if (payload.Length < 2 || payload[0] != (byte)'C' || payload[1] != (byte)'K')
        {
            throw new InvalidDataException("MSZIP block without CK signature.");
        }

        var history = Math.Min(position, MsZipHistory);
        using var input = new MemoryStream(payload.Length + history + 5);

        if (history > 0)
        {
            Span<byte> stored = stackalloc byte[5];
            stored[0] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(stored[1..], (ushort)history);
            BinaryPrimitives.WriteUInt16LittleEndian(stored[3..], (ushort)~history);
            input.Write(stored);
            input.Write(output, position - history, history);
        }

        input.Write(payload[2..]);
        input.Position = 0;

        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        var buffer = new byte[history + length];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = inflate.Read(buffer, read, buffer.Length - read)) > 0)
        {
            read += n;
        }

        if (read != buffer.Length)
        {
            throw new InvalidDataException("MSZIP block is shorter than announced.");
        }

        buffer.AsSpan(history).CopyTo(output.AsSpan(position));
    }

    private readonly record struct FolderInfo(uint DataOffset, int BlockCount, int CompressionType);

    private readonly record struct DataBlock(int Offset, int Compressed, int Uncompressed);
}
