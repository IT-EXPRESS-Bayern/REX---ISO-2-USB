// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Tests.Boot;

/// <summary>Writes small but structurally complete PE images so parsers can be tested without binary fixtures.</summary>
internal sealed class PeBuilder
{
    private const int DosHeaderSize = 0x80;
    private const int SectionAlignment = 0x1000;

    private readonly List<(string Name, byte[] Data, uint? VirtualSize)> _sections = [];
    private readonly List<(byte[] Data, ushort Type)> _certificates = [];
    private (ushort Major, ushort Minor, string Name)? _securityVersion;

    public bool Pe32Plus { get; set; } = true;

    public ushort Machine { get; set; } = 0x8664;

    public ushort Subsystem { get; set; } = 10;

    public uint CheckSum { get; set; }

    public int FileAlignment { get; set; } = 0x200;

    public int NumberOfRvaAndSizes { get; set; } = 16;

    /// <summary>Bytes between the last section and the certificate table (or the end of the file).</summary>
    public byte[] TrailingData { get; set; } = [];

    /// <summary>Sections are written to the file in this order of names while the section table keeps its own.</summary>
    public string[]? FileOrder { get; set; }

    /// <summary>A prebuilt resource section; RVAs inside it are not fixed up. See <see cref="AddBootmgrSecurityVersion"/> for a valid tree.</summary>
    public byte[]? RawResourceSection { get; set; }

    public static PeBuilder Typical() => new PeBuilder()
        .AddSection(".text", Pattern(0x300, 1))
        .AddSection(".data", Pattern(0x150, 2));

    public static byte[] Pattern(int length, int seed)
    {
        var data = new byte[length];
        var state = (uint)seed * 2654435761u + 1;
        for (var i = 0; i < data.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            data[i] = (byte)(state >> 24);
        }

        return data;
    }

    public PeBuilder AddSection(string name, byte[] data, uint? virtualSize = null)
    {
        _sections.Add((name, data, virtualSize));
        return this;
    }

    public PeBuilder AddCertificate(byte[] blob, ushort type = 0x0002)
    {
        _certificates.Add((blob, type));
        return this;
    }

    /// <summary>Adds an RCDATA resource of four bytes: the minor and the major number as little-endian UINT16.</summary>
    public PeBuilder AddBootmgrSecurityVersion(ushort major, ushort minor, string resourceName = "BOOTMGRSECURITYVERSIONNUMBER")
    {
        _securityVersion = (major, minor, resourceName);
        return this;
    }

    public byte[] Build()
    {
        var sections = _sections.ToList();
        var hasResources = _securityVersion is not null || RawResourceSection is not null;
        if (hasResources)
        {
            sections.Add((".rsrc", [0], null));
        }

        var optionalSize = Pe32Plus ? 0xF0 : 0xE0;
        var headersSize = Align(DosHeaderSize + 4 + 20 + optionalSize + 40 * sections.Count, FileAlignment);

        var virtualAddresses = new Dictionary<string, uint>();
        uint nextVirtual = SectionAlignment;
        foreach (var section in sections)
        {
            virtualAddresses[section.Name] = nextVirtual;
            nextVirtual += (uint)Align(Math.Max(section.Data.Length, 1), SectionAlignment);
        }

        // The tree stores the RVA of its data, so it can only be generated once the layout is known.
        if (hasResources)
        {
            var tree = RawResourceSection
                ?? BuildResourceTree(virtualAddresses[".rsrc"], _securityVersion!.Value.Major, _securityVersion.Value.Minor, _securityVersion.Value.Name);
            sections[^1] = (".rsrc", tree, null);
        }

        var order = FileOrder is null
            ? sections.Select(s => s.Name).ToList()
            : [.. FileOrder, .. sections.Select(s => s.Name).Except(FileOrder)];
        var rawOffsets = new Dictionary<string, int>();
        var rawSizes = new Dictionary<string, int>();
        var position = headersSize;
        foreach (var name in order)
        {
            var data = sections.First(s => s.Name == name).Data;
            var size = data.Length == 0 ? 0 : Align(data.Length, FileAlignment);
            rawOffsets[name] = size == 0 ? 0 : position;
            rawSizes[name] = size;
            position += size;
        }

        // Signers align the table to 8 bytes by padding the file; the padding is part of what the digest covers.
        var certificateTable = BuildCertificateTable();
        var dataEnd = position + TrailingData.Length;
        var certificateOffset = certificateTable.Length == 0 ? dataEnd : Align(dataEnd, 8);
        var image = new byte[certificateOffset + certificateTable.Length];

        WriteHeaders(image, sections, rawOffsets, rawSizes, virtualAddresses, headersSize, nextVirtual, certificateOffset, certificateTable.Length, hasResources);
        foreach (var section in sections.Where(s => rawSizes[s.Name] > 0))
        {
            section.Data.CopyTo(image, rawOffsets[section.Name]);
        }

        TrailingData.CopyTo(image, position);
        certificateTable.CopyTo(image, certificateOffset);
        return image;
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    private byte[] BuildCertificateTable()
    {
        using var table = new MemoryStream();
        foreach (var (blob, type) in _certificates)
        {
            var length = 8 + blob.Length;
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0x0200);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), type);
            table.Write(header);
            table.Write(blob);
            table.Write(new byte[Align(length, 8) - length]);
        }

        return table.ToArray();
    }

    private void WriteHeaders(
        byte[] image,
        List<(string Name, byte[] Data, uint? VirtualSize)> sections,
        Dictionary<string, int> rawOffsets,
        Dictionary<string, int> rawSizes,
        Dictionary<string, uint> virtualAddresses,
        int headersSize,
        uint imageSize,
        int certificateOffset,
        int certificateSize,
        bool hasResources)
    {
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), DosHeaderSize);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(image, DosHeaderSize);

        var coff = DosHeaderSize + 4;
        var optionalSize = Pe32Plus ? 0xF0 : 0xE0;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff), Machine);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 2), (ushort)sections.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 16), (ushort)optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(coff + 18), 0x0022);

        // Both optional header formats place these fields at the same offsets once ImageBase (8 or 4 bytes) is passed.
        var opt = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt), Pe32Plus ? (ushort)0x20B : (ushort)0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 16), SectionAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 32), SectionAlignment);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 36), (uint)FileAlignment);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 40), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 48), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 56), imageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 60), (uint)headersSize);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(opt + 64), CheckSum);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(opt + 68), Subsystem);

        var rvaCount = opt + (Pe32Plus ? 108 : 92);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rvaCount), (uint)NumberOfRvaAndSizes);
        var directories = rvaCount + 4;

        if (hasResources && NumberOfRvaAndSizes > 2)
        {
            var rsrcLength = sections.First(s => s.Name == ".rsrc").Data.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(directories + 2 * 8), virtualAddresses[".rsrc"]);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(directories + 2 * 8 + 4), (uint)rsrcLength);
        }

        if (certificateSize > 0 && NumberOfRvaAndSizes > 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(directories + 4 * 8), (uint)certificateOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(directories + 4 * 8 + 4), (uint)certificateSize);
        }

        var table = opt + optionalSize;
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var at = table + 40 * i;
            var nameBytes = Encoding.ASCII.GetBytes(section.Name);
            nameBytes.AsSpan(0, Math.Min(8, nameBytes.Length)).CopyTo(image.AsSpan(at));
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 8), section.VirtualSize ?? (uint)section.Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 12), virtualAddresses[section.Name]);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 16), (uint)rawSizes[section.Name]);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 20), (uint)rawOffsets[section.Name]);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 36), 0x40000040);
        }
    }

    /// <summary>RT_RCDATA (10) -> named resource -> language 1033 -> data entry -> four bytes.</summary>
    private static byte[] BuildResourceTree(uint sectionRva, ushort major, ushort minor, string name)
    {
        const int typeDirectory = 24;
        const int languageDirectory = 48;
        const int dataEntry = 72;
        const int nameString = 88;
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var data = Align(nameString + 2 + nameBytes.Length, 4);
        var tree = new byte[data + 4];

        void Entry(int at, uint nameOrId, uint target)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(at), nameOrId);
            BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(at + 4), target);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(14), 1); // root: one ID entry
        Entry(16, 10, 0x80000000u | typeDirectory);
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(typeDirectory + 12), 1); // one named entry
        Entry(typeDirectory + 16, 0x80000000u | nameString, 0x80000000u | languageDirectory);
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(languageDirectory + 14), 1); // one ID entry
        Entry(languageDirectory + 16, 1033, dataEntry);

        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(dataEntry), sectionRva + (uint)data);
        BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(dataEntry + 4), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(nameString), (ushort)name.Length);
        nameBytes.CopyTo(tree, nameString + 2);
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(data), minor);
        BinaryPrimitives.WriteUInt16LittleEndian(tree.AsSpan(data + 2), major);
        return tree;
    }
}
