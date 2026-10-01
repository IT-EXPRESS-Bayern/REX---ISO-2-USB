// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Tests.Images.Apple;

/// <summary>Hand-built partition tables and volume headers for the cases the system tools cannot produce.</summary>
internal static class AppleTestImages
{
    public const int Sector = 512;

    /// <summary>Writes an Apple Partition Map: the "ER" descriptor in block 0 and one "PM" entry per partition.</summary>
    public static void WriteApm(Span<byte> image, int blockSize, params (string Name, string Type, uint Start, uint Count)[] partitions)
    {
        BinaryPrimitives.WriteUInt16BigEndian(image, 0x4552);
        BinaryPrimitives.WriteUInt16BigEndian(image[2..], (ushort)blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(image[4..], (uint)(image.Length / blockSize));

        for (var i = 0; i < partitions.Length; i++)
        {
            var entry = image.Slice((i + 1) * blockSize, 0x88);
            BinaryPrimitives.WriteUInt16BigEndian(entry, 0x504D);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)partitions.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], partitions[i].Start);
            BinaryPrimitives.WriteUInt32BigEndian(entry[0xC..], partitions[i].Count);
            Encoding.ASCII.GetBytes(partitions[i].Name, entry.Slice(0x10, 32));
            Encoding.ASCII.GetBytes(partitions[i].Type, entry.Slice(0x30, 32));
            BinaryPrimitives.WriteUInt32BigEndian(entry[0x58..], 0x33);
        }
    }

    /// <summary>Writes an HFS+ (or HFSX) volume header 1024 bytes into the volume.</summary>
    public static void WriteHfsPlusHeader(Span<byte> volume, bool caseSensitive = false, uint blockSize = 4096, uint totalBlocks = 1024, uint blessedFolder = 0)
    {
        var header = volume[1024..];
        BinaryPrimitives.WriteUInt16BigEndian(header, caseSensitive ? (ushort)0x4858 : (ushort)0x482B);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], caseSensitive ? (ushort)5 : (ushort)4);
        BinaryPrimitives.WriteUInt32BigEndian(header[40..], blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(header[44..], totalBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(header[80..], blessedFolder);
    }

    /// <summary>Writes a classic HFS master directory block 1024 bytes into the volume.</summary>
    public static void WriteHfsHeader(Span<byte> volume, string name, uint blessedFolder = 0, bool embedHfsPlus = false)
    {
        var mdb = volume[1024..];
        BinaryPrimitives.WriteUInt16BigEndian(mdb, 0x4244);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[18..], 8000);
        BinaryPrimitives.WriteUInt32BigEndian(mdb[20..], 1024);
        BinaryPrimitives.WriteUInt16BigEndian(mdb[28..], 8);
        mdb[36] = (byte)name.Length;
        Encoding.Latin1.GetBytes(name, mdb.Slice(37, name.Length));
        BinaryPrimitives.WriteUInt32BigEndian(mdb[92..], blessedFolder);
        if (embedHfsPlus)
        {
            BinaryPrimitives.WriteUInt16BigEndian(mdb[124..], 0x482B);
            BinaryPrimitives.WriteUInt16BigEndian(mdb[126..], 2);
            BinaryPrimitives.WriteUInt16BigEndian(mdb[128..], 100);
        }
    }

    /// <summary>Writes the APFS container superblock magic and geometry at the start of the volume.</summary>
    public static void WriteApfsSuperblock(Span<byte> volume, uint blockSize = 4096, ulong blockCount = 1000)
    {
        "NXSB"u8.CopyTo(volume[32..]);
        BinaryPrimitives.WriteUInt32LittleEndian(volume[36..], blockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(volume[40..], blockCount);
    }

    public static void WriteMbr(Span<byte> image, params (byte Type, uint Start, uint Count)[] partitions)
    {
        for (var i = 0; i < partitions.Length; i++)
        {
            var entry = image.Slice(446 + (i * 16), 16);
            entry[4] = partitions[i].Type;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], partitions[i].Start);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], partitions[i].Count);
        }

        image[510] = 0x55;
        image[511] = 0xAA;
    }

    public static void WriteGpt(Span<byte> image, params (Guid Type, ulong First, ulong Last)[] partitions)
    {
        var header = image.Slice(Sector, 92);
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], 128);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], 128);

        for (var i = 0; i < partitions.Length; i++)
        {
            var entry = image.Slice((2 * Sector) + (i * 128), 128);
            partitions[i].Type.TryWriteBytes(entry);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], partitions[i].First);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], partitions[i].Last);
            Encoding.Unicode.GetBytes("part" + i, entry.Slice(56, 72));
        }
    }

    public static void WriteIsoDescriptors(Span<byte> image, bool elTorito)
    {
        var primary = image.Slice(16 * 2048, 8);
        primary[0] = 1;
        "CD001"u8.CopyTo(primary[1..]);
        primary[6] = 1;
        if (elTorito)
        {
            var boot = image.Slice(17 * 2048, 39);
            "CD001"u8.CopyTo(boot[1..]);
            boot[6] = 1;
            "EL TORITO SPECIFICATION"u8.CopyTo(boot[7..]);
        }
    }

    public static readonly Guid GptHfsPlus = new("48465300-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid GptApfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid GptRecovery = new("426F6F74-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid GptEsp = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid GptLinux = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    public static readonly Guid GptMicrosoftBasic = new("EBD0A0A2-B9E5-4433-87C0-68B6DAC5E41B");
}
