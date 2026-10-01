// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>
/// DMGs are parsed from untrusted files. Every manipulated number in the headers must end in a
/// <see cref="BootrixException"/> before anything large is allocated or read.
/// </summary>
public class DmgMalformedTests
{
    private const int EntriesOffset = 0xCC;
    private const int EntrySize = 40;

    private static readonly byte[] Plain = ImageTestData.Text(8 * 512, 91);

    // Four chunks: raw, zlib, zero fill, bzip2 - so entry 0 starts at sector 0 and entry 3 ends at sector 32.
    private static List<ChunkSpec> Chunks() =>
    [
        ChunkSpec.Raw(Plain),
        ChunkSpec.Zlib(Plain),
        ChunkSpec.Zero(8),
        ChunkSpec.Bzip2(Plain),
    ];

    private static byte[] ImageWithTable(Action<byte[]> patch, Action<byte[]>? patchTrailer = null)
    {
        var builder = new UdifBuilder
        {
            MutateTable = (_, table) =>
            {
                patch(table);
                return table;
            },
            MutateTrailer = patchTrailer,
        };
        return builder.AddPartition(0, "disk image (Apple_HFS : 1)", 0, Chunks()).Build();
    }

    private static Span<byte> Entry(byte[] table, int index) => table.AsSpan(EntriesOffset + (index * EntrySize), EntrySize);

    private static BootrixException OpenAndReadFails(byte[] image)
    {
        return Assert.Throws<BootrixException>(() =>
        {
            using var reader = DmgReader.Open(new MemoryStream(image));
            reader.CopyTo(Stream.Null);
        });
    }

    [Fact]
    public void Baseline_UnmodifiedImage_Works()
    {
        var image = ImageWithTable(_ => { });

        using var reader = DmgReader.Open(new MemoryStream(image));

        Assert.Equal(32 * 512, reader.Length);
        reader.CopyTo(Stream.Null);
    }

    [Fact]
    public void ChunkOffsetBeyondTheFile_IsTruncation()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[24..], 5_000_000));

        Assert.Equal(ErrorCode.ImageTruncated, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void ChunkLengthBeyondTheFile_IsTruncation()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[32..], 4096 + 3000));

        Assert.Equal(ErrorCode.ImageTruncated, OpenAndReadFails(image).Code);
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x8000000000000000UL)]
    [InlineData(long.MaxValue - 100)]
    public void ChunkOffsetOverflow_IsCorruption(ulong offset)
    {
        var image = ImageWithTable(t =>
        {
            BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[24..], offset);
            BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[32..], 400);
        });

        Assert.Contains(OpenAndReadFails(image).Code, new[] { ErrorCode.ImageCorrupt, ErrorCode.ImageTruncated });
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData(1UL << 62)]
    public void ChunkLengthOverflow_IsCorruption(ulong length)
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[32..], length));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Theory]
    [InlineData(1UL << 40)]
    [InlineData(ulong.MaxValue)]
    [InlineData(1UL << 63)]
    public void CompressedChunkWithHugeSectorCount_IsRejectedBeforeAllocating(ulong sectors)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var image = ImageWithTable(t =>
        {
            BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[16..], sectors);
            BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(0x10), sectors);
        });

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 64 * 1024 * 1024);
    }

    [Fact]
    public void CompressedChunkAboveTheChunkLimit_IsRejected()
    {
        var table = new DmgReaderOptions { MaxChunkBytes = 2 * 512 };
        var image = ImageWithTable(_ => { });

        var ex = Assert.Throws<BootrixException>(() => DmgReader.Open(new MemoryStream(image), options: table));

        Assert.Equal(ErrorCode.ImageCorrupt, ex.Code);
    }

    [Theory]
    [InlineData(1UL << 36)]
    [InlineData(1UL << 50)]
    [InlineData(ulong.MaxValue)]
    public void VolumeLargerThanTheLimit_IsRejected(ulong sectors)
    {
        var image = ImageWithTable(_ => { }, t => BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(0x1EC), sectors));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void PartitionStartBeyondTheLimit_IsRejected()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(8), 1UL << 62));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void HugeZeroFillRun_CostsNothingToOpenOrRead()
    {
        const long sectors = 1L << 33; // 4 TiB
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var image = new UdifBuilder
        {
            Checksums = false,
            MutateTable = (_, table) =>
            {
                BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(0x10), sectors + 8);
                BinaryPrimitives.WriteUInt64BigEndian(Entry(table, 1)[16..], sectors);
                return table;
            },
            MutateTrailer = t => BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(0x1EC), sectors + 8),
        }.AddPartition(0, "disk image (Apple_HFS : 1)", 0, [ChunkSpec.Raw(Plain), ChunkSpec.Zero(8)]).Build();

        using var reader = DmgReader.Open(new MemoryStream(image));
        reader.Position = reader.Length - 4096;
        var tail = new byte[4096];
        reader.ReadExactly(tail);
        reader.Position = 1L << 40;
        reader.ReadExactly(tail);

        Assert.Equal((sectors + 8) * 512, reader.Length);
        Assert.All(tail, b => Assert.Equal(0, b));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < 8 * 1024 * 1024);
    }

    [Fact]
    public void ChunkOutsideItsPartition_IsCorruption()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 3)[8..], 30));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void OverlappingChunks_AreCorruption()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 2)[8..], 4));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void EntryCountLargerThanTheTable_IsCorruption()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0xC8), uint.MaxValue));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void BrokenTableSignature_IsCorruption()
    {
        var image = ImageWithTable(t => t[0] = (byte)'x');

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void UnknownTableVersion_IsUnsupported()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(4), 2));

        Assert.Equal(ErrorCode.ImageUnsupported, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void RawChunkWithWrongLength_IsCorruption()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 0)[32..], 4000));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(10_000_000UL)]
    public void CompressedChunkWithImplausibleLength_IsCorruption(ulong length)
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[32..], length));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void ChunkClaimingMoreSectorsThanItDecodesTo_IsCorruption()
    {
        // The zlib chunk holds 8 sectors; the table says 16 (and the partition grows accordingly).
        var image = ImageWithTable(t =>
        {
            BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[16..], 16);
            BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 2)[8..], 24);
            BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 3)[8..], 32);
            BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(0x10), 40);
        });

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void ChunkDecodingToMoreThanItsSectorCount_IsCorruption()
    {
        var image = ImageWithTable(t => BinaryPrimitives.WriteUInt64BigEndian(Entry(t, 1)[16..], 4));

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Fact]
    public void TrailerPointingBeyondTheFile_IsTruncation()
    {
        var image = DmgFixtures.Build("zlib", template: new UdifBuilder
        {
            MutateTrailer = t => BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(0xD8), 10_000_000_000),
        });

        Assert.Equal(ErrorCode.ImageTruncated, OpenAndReadFails(image).Code);
    }

    [Theory]
    [InlineData(ulong.MaxValue)]
    [InlineData(1UL << 40)]
    public void PropertyListLengthImplausible_IsCorruption(ulong length)
    {
        var image = DmgFixtures.Build("zlib", template: new UdifBuilder
        {
            MutateTrailer = t => BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(0xE0), length),
        });

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }

    [Theory]
    [MemberData(nameof(HostilePropertyLists))]
    public void HostilePropertyList_FailsInAControlledWay(string name, string xml)
    {
        var started = Stopwatch.StartNew();
        var image = DmgFixtures.Build("zlib", template: new UdifBuilder { MutateDirectory = _ => Encoding.UTF8.GetBytes(xml) });

        var ex = OpenAndReadFails(image);

        Assert.True(ex.Code is ErrorCode.ImageCorrupt or ErrorCode.ImageUnsupported, name);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), name);
    }

    public static TheoryData<string, string> HostilePropertyLists => new()
    {
        { "not xml", "this is not a property list" },
        { "no root dict", "<plist version=\"1.0\"><array/></plist>" },
        { "no blkx", "<plist version=\"1.0\"><dict><key>resource-fork</key><dict/></dict></plist>" },
        { "empty blkx", "<plist version=\"1.0\"><dict><key>resource-fork</key><dict><key>blkx</key><array/></dict></dict></plist>" },
        { "blkx without data", "<plist version=\"1.0\"><dict><key>resource-fork</key><dict><key>blkx</key><array><dict/></array></dict></dict></plist>" },
        {
            "invalid base64",
            "<plist version=\"1.0\"><dict><key>resource-fork</key><dict><key>blkx</key><array><dict><key>Data</key><data>!!!notbase64</data></dict></array></dict></dict></plist>"
        },
        {
            "short table",
            "<plist version=\"1.0\"><dict><key>resource-fork</key><dict><key>blkx</key><array><dict><key>Data</key><data>bWlzaA==</data></dict></array></dict></dict></plist>"
        },
        { "entity expansion", EntityBomb() },
        { "external entity", "<?xml version=\"1.0\"?><!DOCTYPE plist [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><plist version=\"1.0\"><dict><key>a</key><string>&x;</string></dict></plist>" },
        { "deep nesting", "<plist version=\"1.0\">" + string.Concat(Enumerable.Repeat("<array>", 5000)) + string.Concat(Enumerable.Repeat("</array>", 5000)) + "</plist>" },
        { "binary plist", "bplist00\u0001\u0002" },
        { "unterminated", "<plist version=\"1.0\"><dict><key>resource-fork</key><dict><key>blkx</key><array>" },
        { "key without value", "<plist version=\"1.0\"><dict><key>resource-fork</key></dict></plist>" },
        { "wrong integer", "<plist version=\"1.0\"><dict><key>a</key><integer>99999999999999999999999</integer></dict></plist>" },
    };

    private static string EntityBomb()
    {
        var builder = new StringBuilder("<?xml version=\"1.0\"?><!DOCTYPE plist [<!ENTITY a0 \"lol\">");
        for (var i = 1; i < 10; i++)
        {
            builder.Append(string.Concat("<!ENTITY a", i, " \"", string.Concat(Enumerable.Repeat($"&a{i - 1};", 10)), "\">"));
        }

        builder.Append("]><plist version=\"1.0\"><dict><key>a</key><string>&a9;</string></dict></plist>");
        return builder.ToString();
    }

    [Fact]
    public void ResourceFork_WithOverlappingHugeResources_FailsInAControlledWay()
    {
        var image = DmgFixtures.Build("zlib", template: new UdifBuilder
        {
            ResourceFork = true,
            MutateDirectory = fork =>
            {
                // Make every reference point at the first resource and enlarge it to the whole fork.
                BinaryPrimitives.WriteUInt32BigEndian(fork.AsSpan(256), (uint)(fork.Length - 300));
                return fork;
            },
        });

        var ex = OpenAndReadFails(image);

        Assert.Equal(ErrorCode.ImageCorrupt, ex.Code);
    }

    [Fact]
    public void ResourceFork_Truncated_FailsInAControlledWay()
    {
        var image = DmgFixtures.Build("zlib", template: new UdifBuilder
        {
            ResourceFork = true,
            MutateDirectory = fork => fork[..(fork.Length - 40)],
        });

        Assert.Equal(ErrorCode.ImageCorrupt, OpenAndReadFails(image).Code);
    }
}
