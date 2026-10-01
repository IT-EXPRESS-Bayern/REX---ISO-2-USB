// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Storage;
using Bootrix.Windows.Storage;

namespace Bootrix.Windows.Tests.Storage;

public class DriveLayoutBuilderTests
{
    private static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private static readonly Guid Esp = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");

    [Fact]
    public void MbrLayoutAlwaysDescribesFourSlots()
    {
        var spec = new LayoutSpec
        {
            Style = LayoutStyle.Mbr,
            DiskSizeBytes = 16L << 30,
            SectorSize = 512,
            MbrSignature = 0x12345678,
            Partitions = [new PartitionSpec { OffsetBytes = 1 << 20, LengthBytes = 8L << 30, MbrType = 0x0C, Active = true }],
        };

        var data = DriveLayoutBuilder.BuildLayout(spec);

        Assert.Equal(48 + 4 * 144, data.Length);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        var layout = DriveLayoutReader.Parse(data);
        Assert.Equal(DiskPartitionStyle.Mbr, layout.Style);
        Assert.Equal("12345678", layout.Signature);
        var partition = Assert.Single(layout.Partitions);
        Assert.Equal(1 << 20, partition.Offset);
        Assert.Equal(8L << 30, partition.Length);
        Assert.Equal("0x0C", partition.Type);
        Assert.Equal(1, data[48 + 33]);
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(48 + 36)));
    }

    [Fact]
    public void UnusedMbrSlotsAreMarkedForRewrite()
    {
        var spec = new LayoutSpec
        {
            Style = LayoutStyle.Mbr,
            DiskSizeBytes = 1L << 30,
            SectorSize = 512,
            Partitions = [],
        };

        var data = DriveLayoutBuilder.BuildLayout(spec);

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(1, data[48 + i * 144 + 28]);
        }
    }

    [Fact]
    public void MoreThanFourMbrPartitionsAreRejected()
    {
        var partitions = Enumerable.Range(0, 5)
            .Select(i => new PartitionSpec { OffsetBytes = (i + 1) << 20, LengthBytes = 1 << 20, MbrType = 0x07 })
            .ToList();

        Assert.Throws<ArgumentException>(() => DriveLayoutBuilder.BuildLayout(
            new LayoutSpec { Style = LayoutStyle.Mbr, DiskSizeBytes = 1L << 30, SectorSize = 512, Partitions = partitions }));
    }

    [Fact]
    public void GptLayoutRoundTripsWithNamesAndTypes()
    {
        var diskId = Guid.NewGuid();
        var spec = new LayoutSpec
        {
            Style = LayoutStyle.Gpt,
            DiskSizeBytes = 32L << 30,
            SectorSize = 512,
            GptDiskId = diskId,
            Partitions =
            [
                new PartitionSpec { OffsetBytes = 1 << 20, LengthBytes = 300 << 20, GptType = Esp, Name = "EFI system partition" },
                new PartitionSpec { OffsetBytes = 301 << 20, LengthBytes = 20L << 30, GptType = BasicData, Name = "INSTALL", GptAttributes = 0x8000000000000000 },
            ],
        };

        var data = DriveLayoutBuilder.BuildLayout(spec);

        Assert.Equal(48 + 2 * 144, data.Length);
        var layout = DriveLayoutReader.Parse(data);
        Assert.Equal(diskId.ToString("D"), layout.Signature);
        Assert.Equal(2, layout.Partitions.Count);
        Assert.True(layout.Partitions[0].IsEfiSystem);
        Assert.Equal("INSTALL", layout.Partitions[1].Name);
        Assert.Equal(0x8000000000000000UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48 + 144 + 64)));
    }

    [Theory]
    [InlineData(512, 17_408L)]
    [InlineData(4096, 24_576L)]
    public void GptUsableRangeLeavesRoomForBothTables(int sector, long expectedFirstUsable)
    {
        var disk = 16L << 30;

        var (start, length) = DriveLayoutBuilder.GptUsableRange(disk, sector);

        Assert.Equal(expectedFirstUsable, start);
        Assert.Equal(0, start % sector);
        // The backup entry array and the backup header sit behind the last usable byte.
        Assert.Equal(disk, start + length + 16384 + sector);
    }

    [Fact]
    public void CreateDiskStructuresMatchTheDocumentedLayout()
    {
        var gpt = new LayoutSpec { Style = LayoutStyle.Gpt, DiskSizeBytes = 1L << 30, SectorSize = 512, GptDiskId = Guid.NewGuid(), Partitions = [] };
        var mbr = new LayoutSpec { Style = LayoutStyle.Mbr, DiskSizeBytes = 1L << 30, SectorSize = 512, MbrSignature = 0xCAFE0001, Partitions = [] };

        var gptData = DriveLayoutBuilder.BuildCreateDisk(gpt);
        var mbrData = DriveLayoutBuilder.BuildCreateDisk(mbr);
        var raw = DriveLayoutBuilder.BuildCreateRawDisk();

        Assert.Equal(24, gptData.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(gptData));
        Assert.Equal(gpt.GptDiskId, new Guid(gptData.AsSpan(4, 16)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(gptData.AsSpan(20)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(mbrData));
        Assert.Equal(0xCAFE0001u, BinaryPrimitives.ReadUInt32LittleEndian(mbrData.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(raw));
    }
}
