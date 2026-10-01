// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Ext;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.FileSystems.Ext;

public class ExtFileAllocatorTests
{
    private const long MiB = 1024 * 1024;

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 3, 14, 9, 26, 53, TimeSpan.Zero));

    private static ExtVolume FormatAndOpen(Stream stream, ExtFileSystemType type, long size)
    {
        ExtFormatter.Format(stream, new ExtFormatOptions { Type = type, BlockSize = 1024, SizeBytes = size, TimeProvider = Clock });
        return ExtVolume.Open(stream, Clock);
    }

    [Fact]
    public void Allocate_OnExt4_SplitsRunsAtGroupMetadataAndKeepsBlocksInOrder()
    {
        var stream = new SparseMemoryStream(512 * MiB);
        var volume = FormatAndOpen(stream, ExtFileSystemType.Ext4, 512 * MiB);

        var file = ExtFileAllocator.Allocate(volume, 100_000);

        Assert.Equal(100_000, file.DataBlocks.Length);
        Assert.Equal(file.DataBlocks.OrderBy(block => block), file.DataBlocks);
        Assert.Equal(file.DataBlocks.Length, file.DataBlocks.Distinct().Count());
        Assert.Equal(ExtInode.FlagExtents, file.InodeFlags);
        var read = ExtExtentTree.Read(file.IBlock, file.DataBlocks.Length, 1024, (block, buffer) => volume.ReadBlock(block, buffer));
        Assert.Equal(file.DataBlocks, read);
    }

    [Fact]
    public void Allocate_OnExt3_PlacesMappingBlocksBetweenData()
    {
        var stream = new SparseMemoryStream(64 * MiB);
        var volume = FormatAndOpen(stream, ExtFileSystemType.Ext3, 64 * MiB);

        var file = ExtFileAllocator.Allocate(volume, 1000);

        Assert.Equal(0u, file.InodeFlags);
        Assert.Equal(1000 + ExtBlockMap.MappingBlockCount(1000, 1024), file.TotalBlocks);
        var read = ExtBlockMap.Read(file.IBlock, 1000, 1024, (block, buffer) => volume.ReadBlock(block, buffer));
        Assert.Equal(file.DataBlocks, read);
    }

    [Fact]
    public void Allocate_BeyondFreeSpace_ThrowsWithoutTakingBlocks()
    {
        var stream = new SparseMemoryStream(16 * MiB);
        var volume = FormatAndOpen(stream, ExtFileSystemType.Ext2, 16 * MiB);
        var free = volume.Superblock.FreeBlocksCount;

        Assert.Throws<Bootrix.Core.Errors.BootrixException>(() => ExtFileAllocator.Allocate(volume, free + 1));

        Assert.Equal(free, volume.Superblock.FreeBlocksCount);
        Assert.Equal(100, ExtFileAllocator.Allocate(volume, 100).DataBlocks.Length);
    }

    [ExtToolFact]
    public void Allocate_NeedingTwoExtentLeaves_IsAcceptedByFsck()
    {
        using var image = new TempImage(4096 * MiB);
        var volume = FormatAndOpen(image.Stream, ExtFileSystemType.Ext4, 4096 * MiB);
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();

        var file = ExtFileAllocator.Allocate(volume, 1_000_000);
        var number = volume.AllocateInode(directory: false);
        volume.WriteNewInode(number, new ExtInode
        {
            Mode = ExtInode.ModeRegular | 0x1A4,
            LinksCount = 1,
            Size = 1_000_000L * 1024,
            Sectors = file.TotalBlocks * 2,
            Flags = file.InodeFlags,
            IBlock = file.IBlock,
            AccessTime = now,
            ChangeTime = now,
            ModificationTime = now,
            CreationTime = now,
            ExtraSize = volume.InodeExtraSize,
        });
        var root = volume.ReadInode(ExtVolume.RootInode);
        var rootBlock = ExtExtentTree.Read(root.IBlock, 1, 1024, (block, buffer) => volume.ReadBlock(block, buffer))[0];
        var directory = new byte[1024];
        volume.ReadBlock(rootBlock, directory);
        Assert.True(ExtDirectory.TryAdd(directory, "huge"u8, number, ExtDirectory.TypeRegular));
        volume.WriteBlock(rootBlock, directory);
        volume.Flush();
        var path = image.Close();

        var fsck = ExtTools.Fsck(path);
        var stat = ExtTools.Debugfs(path, "stat /huge");
        Assert.True(fsck.ExitCode == 0, fsck.All);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(stat, @"\(ETB0\)"));
    }
}
