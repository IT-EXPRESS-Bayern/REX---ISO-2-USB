// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Ext;

namespace Bootrix.Core.Tests.FileSystems.Ext;

public class ExtStructureTests
{
    [Fact]
    public void Crc16_MatchesPublishedCheckValue()
    {
        // CRC-16/MODBUS (reflected 0x8005, init 0xFFFF) is the same function as the kernel's crc16 with ~0 as seed.
        Assert.Equal(0x4B37, Crc16.Update(0xFFFF, "123456789"u8));
    }

    [Fact]
    public void Layout_PlacesSuperblockBackupsInSparseGroups()
    {
        var layout = ExtLayout.Plan(8L << 30, 1024, 16384, 256);

        var groups = Enumerable.Range(0, layout.GroupCount).Where(layout.HasSuperBackup);

        Assert.Equal(1024, layout.GroupCount);
        Assert.Equal([0, 1, 3, 5, 7, 9, 25, 27, 49, 81, 125, 243, 343, 625, 729], groups);
    }

    [Fact]
    public void Layout_ComputesGeometryOfSmallVolume()
    {
        var layout = ExtLayout.Plan(64L << 20, 1024, 4096, 256);

        Assert.Equal(65536, layout.BlockCount);
        Assert.Equal(1, layout.FirstDataBlock);
        Assert.Equal(8, layout.GroupCount);
        Assert.Equal(2048, layout.InodesPerGroup);
        Assert.Equal(512, layout.InodeTableBlocks);
        Assert.Equal(1, layout.GdtBlocks);
        Assert.Equal(1 + 1 + 1, layout.BlockBitmapLocation(0));
        Assert.Equal(8193L + 2, layout.BlockBitmapLocation(1));
        Assert.Equal(2L * 8192 + 1, layout.BlockBitmapLocation(2));
    }

    [Fact]
    public void Layout_InodesPerGroupFillWholeBlocksAndBitmapBytes()
    {
        var layout = ExtLayout.Plan(100L << 20, 4096, 65536, 256);

        Assert.Equal(0, layout.InodesPerGroup % 8);
        Assert.Equal(0, layout.InodesPerGroup * layout.InodeSize % layout.BlockSize);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1_700_000_000L)]
    [InlineData(2_147_483_648L)]
    [InlineData(4_294_967_296L)]
    [InlineData(9_000_000_000L)]
    [InlineData(-86_400L)]
    public void Inode_TimestampsRoundTripThroughTheExtraEpochBits(long seconds)
    {
        var inode = new ExtInode
        {
            ModificationTime = seconds,
            ChangeTime = seconds,
            AccessTime = seconds,
            CreationTime = seconds,
            ExtraSize = ExtInode.DefaultExtraSize,
        };
        var raw = new byte[256];

        inode.WriteTo(raw);
        var read = ExtInode.Read(raw);

        Assert.Equal(seconds, read.ModificationTime);
        Assert.Equal(seconds, read.ChangeTime);
        Assert.Equal(seconds, read.AccessTime);
        Assert.Equal(seconds, read.CreationTime);
    }

    [Fact]
    public void Inode_WithoutExtraFieldsTruncatesTimestampsAndSkipsCreationTime()
    {
        var inode = new ExtInode { ModificationTime = 4_294_967_296L + 5, CreationTime = 7 };
        var raw = new byte[128];

        inode.WriteTo(raw);
        var read = ExtInode.Read(raw);

        Assert.Equal(5, read.ModificationTime);
        Assert.Equal(0, read.CreationTime);
    }

    [Fact]
    public void Inode_RoundTripsOwnerSizeAndBlockPointers()
    {
        var iblock = Enumerable.Range(0, ExtInode.IBlockSize).Select(i => (byte)i).ToArray();
        var inode = new ExtInode
        {
            Mode = 0x81A4,
            Uid = 0x12345,
            Gid = 0x2ABCD,
            Size = (5L << 32) + 77,
            LinksCount = 3,
            Sectors = 4096,
            Flags = ExtInode.FlagExtents,
            IBlock = iblock,
            ExtraSize = ExtInode.DefaultExtraSize,
        };
        var raw = new byte[256];

        inode.WriteTo(raw);
        var read = ExtInode.Read(raw);

        Assert.Equal(inode.Mode, read.Mode);
        Assert.Equal(inode.Uid, read.Uid);
        Assert.Equal(inode.Gid, read.Gid);
        Assert.Equal(inode.Size, read.Size);
        Assert.Equal(inode.LinksCount, read.LinksCount);
        Assert.Equal(inode.Sectors, read.Sectors);
        Assert.Equal(inode.Flags, read.Flags);
        Assert.Equal(iblock, read.IBlock);
    }

    [Fact]
    public void Inode_OverlayKeepsUnknownBytes()
    {
        var raw = new byte[256];
        raw[0xE0] = 0xAB;
        BitConverter.TryWriteBytes(raw.AsSpan(0x80), (ushort)32);

        new ExtInode { Mode = 0x41ED, ExtraSize = 32 }.WriteTo(raw);

        Assert.Equal(0xAB, raw[0xE0]);
    }

    [Fact]
    public void GroupDescriptor_RoundTripsAndChecksumIgnoresStoredChecksum()
    {
        var descriptor = new ExtGroupDescriptor
        {
            BlockBitmap = 5,
            InodeBitmap = 6,
            InodeTable = 7,
            FreeBlocks = 100,
            FreeInodes = 200,
            UsedDirectories = 3,
            Flags = ExtGroupDescriptor.FlagInodeUninit,
            InodeTableUnused = 190,
        };
        var uuid = new byte[16];
        var checksum = descriptor.ComputeChecksum(uuid, 4);
        descriptor.Checksum = 0xFFFF;
        var raw = new byte[ExtGroupDescriptor.Size];
        descriptor.Write(raw);

        var read = ExtGroupDescriptor.Read(raw);

        Assert.Equal(descriptor.InodeTable, read.InodeTable);
        Assert.Equal(descriptor.InodeTableUnused, read.InodeTableUnused);
        Assert.Equal(checksum, read.ComputeChecksum(uuid, 4));
        Assert.NotEqual(checksum, read.ComputeChecksum(uuid, 5));
    }

    [Fact]
    public void Bitmap_FindsFirstClearBitWithinLimit()
    {
        var bitmap = new byte[4];
        ExtBitmap.SetRange(bitmap, 0, 19);

        Assert.Equal(19, ExtBitmap.FindClear(bitmap, 0, 32));
        Assert.Equal(-1, ExtBitmap.FindClear(bitmap, 0, 19));
        Assert.Equal(25, ExtBitmap.FindClear(bitmap, 25, 32));

        ExtBitmap.Clear(bitmap, 3);
        Assert.Equal(3, ExtBitmap.FindClear(bitmap, 0, 32));
    }
}
