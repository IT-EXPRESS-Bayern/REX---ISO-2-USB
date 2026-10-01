// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Bootrix.Core.Catalog.Microsoft;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class CabinetArchiveTests
{
    private static byte[] Noise(int length, int seed = 7)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] ExtractOnly(byte[] cabinet)
    {
        var archive = CabinetArchive.Parse(cabinet);
        return archive.Extract(Assert.Single(archive.Entries));
    }

    [Fact]
    public void Extract_StoredFolderWithSeveralBlocksAndFiles_ReturnsEachFileSeparately()
    {
        var text = Encoding.ASCII.GetBytes("hello cabinet");
        var cabinet = new CabinetBuilder()
            .Folder(CabinetBuilder.Stored, (text[..4], 4), (text[4..], 9))
            .Folder(CabinetBuilder.Stored, ("tail"u8.ToArray(), 4))
            .File("a.txt", 0, 0, 5)
            .File("b.bin", 0, 5, 8)
            .File("dirü.xml", 1, 0, 4, utf8: true)
            .Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Equal(["a.txt", "b.bin", "dirü.xml"], archive.Entries.Select(e => e.Name));
        Assert.Equal("hello", Encoding.ASCII.GetString(archive.Extract(archive.Entries[0])));
        Assert.Equal(" cabinet", Encoding.ASCII.GetString(archive.Extract(archive.Entries[1])));
        Assert.Equal("tail", Encoding.ASCII.GetString(archive.Extract(archive.Entries[2])));
    }

    [Fact]
    public void Extract_ChecksumMismatch_IsRejected()
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 4)).File("f", 0, 0, 4).Build();
        cabinet[^1] ^= 1;

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Entries[0]));
    }

    [Fact]
    public void Extract_BlockWithoutChecksum_IsAccepted()
    {
        var cabinet = new CabinetBuilder { Checksums = false }.Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 4)).File("f", 0, 0, 4).Build();
        cabinet[^1] ^= 1;

        Assert.Equal(4, ExtractOnly(cabinet).Length);
    }

    [Fact]
    public void Extract_FileBeyondFolderEnd_IsRejected()
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 4)).File("f", 0, 2, 4).Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Entries[0]));
    }

    [Fact]
    public void Extract_StoredBlockThatChangesSize_IsRejected()
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 8)).File("f", 0, 0, 8).Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Entries[0]));
    }

    [Fact]
    public void Extract_MsZipBlocks_ResolveReferencesIntoThePreviousBlock()
    {
        var first = "0123456789ABCDEF"u8.ToArray();
        var second = MsZipBlockWithMatch(literal: (byte)'Z', length: 8, distance: 8);

        var cabinet = new CabinetBuilder()
            .Folder(CabinetBuilder.MsZip, (MsZipBlock(first), first.Length), (second, 9))
            .File("f", 0, 0, 25)
            .Build();

        // After "…89ABCDEF" and the literal Z, a copy of eight bytes at distance eight is "9ABCDEFZ".
        Assert.Equal("0123456789ABCDEFZ9ABCDEFZ", Encoding.ASCII.GetString(ExtractOnly(cabinet)));
    }

    [Fact]
    public void Extract_MsZipBlockWithoutSignature_IsRejected()
    {
        var cabinet = new CabinetBuilder()
            .Folder(CabinetBuilder.MsZip, ("XXdata"u8.ToArray(), 4))
            .File("f", 0, 0, 4)
            .Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Entries[0]));
    }

    [Fact]
    public void Extract_MsZipBlockShorterThanAnnounced_IsRejected()
    {
        var data = "short"u8.ToArray();
        var cabinet = new CabinetBuilder()
            .Folder(CabinetBuilder.MsZip, (MsZipBlock(data), 40))
            .File("f", 0, 0, 40)
            .Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Entries[0]));
    }

    [Fact]
    public void Extract_LzxUncompressedBlockAcrossTwoFrames_ReturnsTheRawBytes()
    {
        var data = Noise(40000);
        var frame1 = new LzxFrameWriter().StreamHeader().UncompressedBlock(40000).Raw(data.AsSpan(..32768)).ToArray();
        var frame2 = new LzxFrameWriter().Raw(data.AsSpan(32768..)).ToArray();

        var cabinet = new CabinetBuilder()
            .Folder(CabinetBuilder.Lzx(15), (frame1, 32768), (frame2, 7232))
            .File("f", 0, 0, 40000)
            .Build();

        Assert.Equal(data, ExtractOnly(cabinet));
    }

    [Fact]
    public void Extract_LzxOddSizedUncompressedBlocks_SkipThePaddingByte()
    {
        byte[] a = [1, 2, 3, 4, 5];
        var b = Noise(11);
        var frame = new LzxFrameWriter()
            .StreamHeader()
            .UncompressedBlock(a.Length).Raw(a).Pad()
            .UncompressedBlock(b.Length).Raw(b).Pad()
            .ToArray();

        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Lzx(16), (frame, 16)).File("f", 0, 0, 16).Build();

        Assert.Equal([.. a, .. b], ExtractOnly(cabinet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extract_LzxOddBlockEndingAtFrameEnd_FindsThePaddingByteInEitherFrame(bool padStartsNextFrame)
    {
        // A block of one byte, then an odd block that ends exactly where the first 32 KiB frame ends.
        var head = Noise(1, seed: 1);
        var body = Noise(32767, seed: 2);
        var tail = Noise(3, seed: 3);

        var frame1 = new LzxFrameWriter().StreamHeader()
            .UncompressedBlock(1).Raw(head).Pad()
            .UncompressedBlock(32767).Raw(body);
        var frame2 = new LzxFrameWriter();
        if (padStartsNextFrame)
        {
            frame2.Pad();
        }
        else
        {
            frame1.Pad();
        }

        frame2.UncompressedBlock(3).Raw(tail).Pad();

        var cabinet = new CabinetBuilder()
            .Folder(CabinetBuilder.Lzx(17), (frame1.ToArray(), 32768), (frame2.ToArray(), 3))
            .File("f", 0, 0, 32771)
            .Build();

        Assert.Equal([.. head, .. body, .. tail], ExtractOnly(cabinet));
    }

    [Fact]
    public void Extract_LzxWithCallTranslation_RestoresRelativeOperands()
    {
        const int fileSize = 65536;
        var data = new byte[100];
        data[20] = 0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(21), 1000);
        data[40] = 0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(41), -5);
        data[60] = 0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(61), fileSize + 7);

        // Too close to the end of the frame to hold a complete operand plus the encoder's safety margin.
        data[95] = 0xE8;

        var frame = new LzxFrameWriter().StreamHeader(fileSize).UncompressedBlock(100).Raw(data).ToArray();
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Lzx(15), (frame, 100)).File("f", 0, 0, 100).Build();

        var result = ExtractOnly(cabinet);

        Assert.Equal(1000 - 20, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(21)));
        Assert.Equal(-5 + fileSize, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(41)));
        Assert.Equal(fileSize + 7, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(61)));
        Assert.Equal(0xE8, result[95]);
    }

    [Fact]
    public void Extract_LzxWithoutCallTranslationFlag_LeavesE8BytesAlone()
    {
        var data = new byte[64];
        data[10] = 0xE8;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(11), 500);

        var frame = new LzxFrameWriter().StreamHeader().UncompressedBlock(64).Raw(data).ToArray();
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Lzx(15), (frame, 64)).File("f", 0, 0, 64).Build();

        Assert.Equal(data, ExtractOnly(cabinet));
    }

    [Fact]
    public void Extract_LzxBlockType0_IsRejected()
    {
        var frame = new LzxFrameWriter().StreamHeader().BlockHeader(0, 10).ToArray();
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Lzx(15), (frame, 10)).File("f", 0, 0, 10).Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Entries[0]));
    }

    [Fact]
    public void Extract_LzxFrameThatEndsInsideTheRawData_IsRejected()
    {
        var frame = new LzxFrameWriter().StreamHeader().UncompressedBlock(50).Raw(Noise(20)).ToArray();
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Lzx(15), (frame, 50)).File("f", 0, 0, 50).Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Entries[0]));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(22)]
    public void Extract_LzxWindowOutsideTheSpecification_IsNotSupported(int windowBits)
    {
        var frame = new LzxFrameWriter().StreamHeader().UncompressedBlock(4).Raw(Noise(4)).ToArray();
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Lzx(windowBits), (frame, 4)).File("f", 0, 0, 4).Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<NotSupportedException>(() => archive.Extract(archive.Entries[0]));
    }

    [Fact]
    public void Extract_QuantumFolder_IsNotSupported()
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Quantum, ("data"u8.ToArray(), 4)).File("f", 0, 0, 4).Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<NotSupportedException>(() => archive.Extract(archive.Entries[0]));
    }

    [Fact]
    public void Extract_UnknownCompressionType_IsRejected()
    {
        var cabinet = new CabinetBuilder().Folder(7, ("data"u8.ToArray(), 4)).File("f", 0, 0, 4).Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Throws<InvalidDataException>(() => archive.Extract(archive.Entries[0]));
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0002)]
    public void Parse_CabinetSetMember_IsNotSupported(int flags)
    {
        var cabinet = new CabinetBuilder { Flags = (ushort)flags }.Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 4)).File("f", 0, 0, 4).Build();

        Assert.Throws<NotSupportedException>(() => CabinetArchive.Parse(cabinet));
    }

    [Fact]
    public void Parse_FileContinuedFromAnotherCabinet_IsNotSupported()
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 4)).File("f", 0xFFFD, 0, 4).Build();

        Assert.Throws<NotSupportedException>(() => CabinetArchive.Parse(cabinet));
    }

    [Fact]
    public void Parse_FileInMissingFolder_IsRejected()
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 4)).File("f", 3, 0, 4).Build();

        Assert.Throws<InvalidDataException>(() => CabinetArchive.Parse(cabinet));
    }

    [Fact]
    public void Parse_WrongSignature_IsRejected()
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 4)).File("f", 0, 0, 4).Build();
        cabinet[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => CabinetArchive.Parse(cabinet));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(60)]
    public void Parse_TruncatedHeaders_AreRejected(int length)
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, ("data"u8.ToArray(), 4)).File("file.txt", 0, 0, 4).Build();

        Assert.Throws<InvalidDataException>(() => CabinetArchive.Parse(cabinet[..length]));
    }

    private static byte[] MsZipBlock(byte[] data)
    {
        using var packed = new MemoryStream();
        packed.Write("CK"u8);
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return packed.ToArray();
    }

    /// <summary>A hand-assembled fixed-Huffman deflate block: one literal, one match, end of block.</summary>
    private static byte[] MsZipBlockWithMatch(byte literal, int length, int distance)
    {
        var bits = new List<bool>();

        void Value(int value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                bits.Add(((value >> i) & 1) == 1);
            }
        }

        void Code(int code, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                bits.Add(((code >> i) & 1) == 1);
            }
        }

        Assert.InRange(length, 3, 10);
        Assert.InRange(distance, 7, 8);

        Value(1, 1);
        Value(1, 2);
        Code(0x30 + literal, 8);
        Code(length - 3 + 1, 7);
        Code(5, 5);
        Value(distance - 7, 1);
        Code(0, 7);

        var bytes = new byte[(bits.Count + 7) / 8 + 2];
        bytes[0] = (byte)'C';
        bytes[1] = (byte)'K';
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[2 + i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return bytes;
    }
}
