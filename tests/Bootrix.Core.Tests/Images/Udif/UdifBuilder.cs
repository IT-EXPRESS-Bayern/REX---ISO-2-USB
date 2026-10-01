// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security;
using System.Text;
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>
/// Writes UDIF images the way hdiutil lays them out (data fork, XML property list, trailer), including the
/// old variants with the trailer in front and the block tables in a binary resource fork.
/// Used because no Mac is available to create fixtures.
/// </summary>
internal sealed class UdifBuilder
{
    private readonly List<PartitionSpec> _partitions = [];

    /// <summary>Put the 512-byte trailer in front of the data fork instead of behind the property list.</summary>
    public bool TrailerAtFront { get; init; }

    /// <summary>Bytes before a front trailer, as a MacBinary wrapper would add.</summary>
    public int Prefix { get; init; }

    /// <summary>Store the block tables in a classic resource fork and leave the XML length at zero.</summary>
    public bool ResourceFork { get; init; }

    /// <summary>Add the "+beg" and "+end" comment entries hdiutil writes.</summary>
    public bool Comments { get; init; }

    public bool Checksums { get; init; } = true;

    public uint ImageVariant { get; init; } = 1;

    /// <summary>Lets a test rewrite the raw block table of the partition with the given index.</summary>
    public Func<int, byte[], byte[]>? MutateTable { get; init; }

    public Action<byte[]>? MutateTrailer { get; init; }

    /// <summary>Lets a test replace the XML property list (or resource fork) before the trailer is computed for it.</summary>
    public Func<byte[], byte[]>? MutateDirectory { get; init; }

    public static UdifBuilder FromVolume(
        byte[] volume,
        IReadOnlyList<(string Name, long Start, long Count)> partitions,
        int chunkSectors,
        Func<int, byte[], ChunkSpec> encode,
        UdifBuilder? template = null)
    {
        var builder = template ?? new UdifBuilder();
        var chunkIndex = 0;
        for (var p = 0; p < partitions.Count; p++)
        {
            var (name, start, count) = partitions[p];
            var chunks = new List<ChunkSpec>();
            for (var done = 0L; done < count; done += chunkSectors)
            {
                var sectors = (int)Math.Min(chunkSectors, count - done);
                var data = volume.AsSpan((int)((start + done) * 512), sectors * 512).ToArray();
                chunks.Add(encode(chunkIndex++, data));
            }

            builder.AddPartition(p - 1, name, start, chunks);
        }

        return builder;
    }

    public UdifBuilder AddPartition(int id, string name, long startSector, IEnumerable<ChunkSpec> chunks)
    {
        _partitions.Add(new PartitionSpec(id, name, startSector, [.. chunks]));
        return this;
    }

    public byte[] Build()
    {
        var dataFork = new MemoryStream();
        var tables = new List<byte[]>();
        var partitionCrcs = new List<uint>();

        for (var p = 0; p < _partitions.Count; p++)
        {
            var partition = _partitions[p];
            var offsets = new List<long>();
            var crc = new TestCrc32();
            foreach (var chunk in partition.Chunks)
            {
                offsets.Add(dataFork.Position);
                dataFork.Write(chunk.Stored);
                if (chunk.Type == UdifChunkType.ZeroFill)
                {
                    crc.AppendZeros(chunk.SectorCount * 512);
                }
                else if (chunk.Plain is not null)
                {
                    crc.Append(chunk.Plain);
                }
            }

            partitionCrcs.Add(crc.Value);
            var table = BuildTable(partition, offsets, Checksums ? crc.Value : null);
            tables.Add(MutateTable?.Invoke(p, table) ?? table);
        }

        var dataBytes = dataFork.ToArray();
        var directory = ResourceFork ? BuildResourceFork(tables) : BuildPropertyList(tables);
        directory = MutateDirectory?.Invoke(directory) ?? directory;

        var trailer = new byte[512];
        WriteTrailer(trailer, dataBytes, directory.Length, partitionCrcs);
        MutateTrailer?.Invoke(trailer);

        using var file = new MemoryStream();
        if (TrailerAtFront)
        {
            file.Write(new byte[Prefix]);
            file.Write(trailer);
            file.Write(dataBytes);
            file.Write(directory);
        }
        else
        {
            file.Write(dataBytes);
            file.Write(directory);
            file.Write(trailer);
        }

        return file.ToArray();
    }

    private void WriteTrailer(byte[] trailer, byte[] dataFork, int directoryLength, List<uint> partitionCrcs)
    {
        var data = trailer.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x6B6F6C79);
        BinaryPrimitives.WriteUInt32BigEndian(data[4..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(data[8..], 512);
        BinaryPrimitives.WriteUInt32BigEndian(data[0x0C..], 1);

        var dataOffset = TrailerAtFront ? 512L : 0L;
        BinaryPrimitives.WriteUInt64BigEndian(data[0x18..], (ulong)dataOffset);
        BinaryPrimitives.WriteUInt64BigEndian(data[0x20..], (ulong)dataFork.Length);
        var directoryOffset = dataOffset + dataFork.Length;
        if (ResourceFork)
        {
            BinaryPrimitives.WriteUInt64BigEndian(data[0x28..], (ulong)directoryOffset);
            BinaryPrimitives.WriteUInt64BigEndian(data[0x30..], (ulong)directoryLength);
        }
        else
        {
            BinaryPrimitives.WriteUInt64BigEndian(data[0xD8..], (ulong)directoryOffset);
            BinaryPrimitives.WriteUInt64BigEndian(data[0xE0..], (ulong)directoryLength);
        }

        BinaryPrimitives.WriteUInt32BigEndian(data[0x3C..], 1);
        if (Checksums)
        {
            WriteChecksum(data[0x50..], TestCrc32.Compute(dataFork));
            var master = new TestCrc32();
            Span<byte> word = stackalloc byte[4];
            foreach (var crc in partitionCrcs)
            {
                BinaryPrimitives.WriteUInt32BigEndian(word, crc);
                master.Append(word);
            }

            WriteChecksum(data[0x160..], master.Value);
        }

        BinaryPrimitives.WriteUInt32BigEndian(data[0x1E8..], ImageVariant);
        var end = _partitions.Max(p => p.StartSector + p.Chunks.Sum(c => c.SectorCount));
        BinaryPrimitives.WriteUInt64BigEndian(data[0x1EC..], (ulong)end);
    }

    private static void WriteChecksum(Span<byte> destination, uint crc)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination, 2);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], crc);
    }

    private byte[] BuildTable(PartitionSpec partition, List<long> offsets, uint? crc)
    {
        var entries = new List<byte[]>();
        long sector = 0;
        if (Comments)
        {
            entries.Add(Entry(0x7FFFFFFE, 0x2B626567, 0, 0, 0, 0));
        }

        for (var i = 0; i < partition.Chunks.Count; i++)
        {
            var chunk = partition.Chunks[i];
            entries.Add(Entry((uint)chunk.Type, 0, sector, chunk.SectorCount, chunk.HasData ? offsets[i] : 0, chunk.Stored.Length));
            sector += chunk.SectorCount;
        }

        if (Comments)
        {
            entries.Add(Entry(0x7FFFFFFE, 0x2B656E64, sector, 0, 0, 0));
        }

        entries.Add(Entry(0xFFFFFFFF, 0, sector, 0, 0, 0));

        var table = new byte[0xCC + (entries.Count * 40)];
        var data = table.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(data, 0x6D697368);
        BinaryPrimitives.WriteUInt32BigEndian(data[4..], 1);
        BinaryPrimitives.WriteUInt64BigEndian(data[8..], (ulong)partition.StartSector);
        BinaryPrimitives.WriteUInt64BigEndian(data[0x10..], (ulong)sector);
        BinaryPrimitives.WriteUInt32BigEndian(data[0x20..], (uint)partition.Chunks.Max(c => c.SectorCount));
        BinaryPrimitives.WriteUInt32BigEndian(data[0x24..], 0x80000000 | (uint)partition.Id);
        if (crc is { } value)
        {
            WriteChecksum(data[0x40..], value);
        }

        BinaryPrimitives.WriteUInt32BigEndian(data[0xC8..], (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].CopyTo(data[(0xCC + (i * 40))..]);
        }

        return table;
    }

    private static byte[] Entry(uint type, uint comment, long sector, long count, long offset, long length)
    {
        var entry = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(entry, type);
        BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(4), comment);
        BinaryPrimitives.WriteUInt64BigEndian(entry.AsSpan(8), (ulong)sector);
        BinaryPrimitives.WriteUInt64BigEndian(entry.AsSpan(16), (ulong)count);
        BinaryPrimitives.WriteUInt64BigEndian(entry.AsSpan(24), (ulong)offset);
        BinaryPrimitives.WriteUInt64BigEndian(entry.AsSpan(32), (ulong)length);
        return entry;
    }

    private byte[] BuildPropertyList(List<byte[]> tables)
    {
        var xml = new StringBuilder();
        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        xml.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        xml.Append("<plist version=\"1.0\">\n<dict>\n\t<key>resource-fork</key>\n\t<dict>\n\t\t<key>blkx</key>\n\t\t<array>\n");
        for (var p = 0; p < _partitions.Count; p++)
        {
            var name = SecurityElement.Escape(_partitions[p].Name);
            xml.Append("\t\t\t<dict>\n\t\t\t\t<key>Attributes</key>\n\t\t\t\t<string>0x0050</string>\n");
            xml.Append("\t\t\t\t<key>CFName</key>\n\t\t\t\t<string>").Append(name).Append("</string>\n");
            xml.Append("\t\t\t\t<key>Data</key>\n\t\t\t\t<data>\n");
            var base64 = Convert.ToBase64String(tables[p]);
            for (var i = 0; i < base64.Length; i += 52)
            {
                xml.Append("\t\t\t\t").Append(base64.AsSpan(i, Math.Min(52, base64.Length - i))).Append('\n');
            }

            xml.Append("\t\t\t\t</data>\n");
            xml.Append("\t\t\t\t<key>ID</key>\n\t\t\t\t<string>").Append(_partitions[p].Id).Append("</string>\n");
            xml.Append("\t\t\t\t<key>Name</key>\n\t\t\t\t<string>").Append(name).Append("</string>\n\t\t\t</dict>\n");
        }

        xml.Append("\t\t</array>\n\t</dict>\n</dict>\n</plist>\n");
        return Encoding.UTF8.GetBytes(xml.ToString());
    }

    // Classic resource fork: 256-byte header, resource data (length-prefixed), then the resource map.
    private byte[] BuildResourceFork(List<byte[]> tables)
    {
        using var data = new MemoryStream();
        var dataOffsets = new List<int>();
        Span<byte> length = stackalloc byte[4];
        foreach (var table in tables)
        {
            dataOffsets.Add((int)data.Position);
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)table.Length);
            data.Write(length);
            data.Write(table);
        }

        using var names = new MemoryStream();
        var nameOffsets = new List<int>();
        foreach (var partition in _partitions)
        {
            nameOffsets.Add((int)names.Position);
            var bytes = Encoding.Latin1.GetBytes(partition.Name);
            names.WriteByte((byte)bytes.Length);
            names.Write(bytes);
        }

        const int typeListOffset = 28;
        var referenceListOffset = 2 + 8;
        var nameListOffset = typeListOffset + referenceListOffset + (tables.Count * 12);
        var map = new byte[nameListOffset + (int)names.Length];
        var mapSpan = map.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(mapSpan[24..], typeListOffset);
        BinaryPrimitives.WriteUInt16BigEndian(mapSpan[26..], (ushort)nameListOffset);
        BinaryPrimitives.WriteUInt16BigEndian(mapSpan[typeListOffset..], 0);
        "blkx"u8.CopyTo(mapSpan[(typeListOffset + 2)..]);
        BinaryPrimitives.WriteUInt16BigEndian(mapSpan[(typeListOffset + 6)..], (ushort)(tables.Count - 1));
        BinaryPrimitives.WriteUInt16BigEndian(mapSpan[(typeListOffset + 8)..], (ushort)referenceListOffset);
        for (var i = 0; i < tables.Count; i++)
        {
            var reference = mapSpan[(typeListOffset + referenceListOffset + (i * 12))..];
            BinaryPrimitives.WriteInt16BigEndian(reference, (short)_partitions[i].Id);
            BinaryPrimitives.WriteUInt16BigEndian(reference[2..], (ushort)nameOffsets[i]);
            reference[5] = (byte)(dataOffsets[i] >> 16);
            reference[6] = (byte)(dataOffsets[i] >> 8);
            reference[7] = (byte)dataOffsets[i];
        }

        names.ToArray().CopyTo(map, nameListOffset);

        var fork = new byte[256 + (int)data.Length + map.Length];
        BinaryPrimitives.WriteUInt32BigEndian(fork, 256);
        BinaryPrimitives.WriteUInt32BigEndian(fork.AsSpan(4), (uint)(256 + data.Length));
        BinaryPrimitives.WriteUInt32BigEndian(fork.AsSpan(8), (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(fork.AsSpan(12), (uint)map.Length);

        // The map starts with a copy of the fork header.
        fork.AsSpan(0, 16).CopyTo(map);
        data.ToArray().CopyTo(fork, 256);
        map.CopyTo(fork, 256 + (int)data.Length);
        return fork;
    }

    private sealed record PartitionSpec(int Id, string Name, long StartSector, List<ChunkSpec> Chunks);
}
