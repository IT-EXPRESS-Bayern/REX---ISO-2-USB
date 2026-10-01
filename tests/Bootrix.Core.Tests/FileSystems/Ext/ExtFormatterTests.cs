// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Ext;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.FileSystems.Ext;

public class ExtFormatterTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    private static readonly Guid FixedUuid = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
    private static readonly DateTimeOffset FixedTime = new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);

    private static ExtFormatOptions Options(ExtFileSystemType type = ExtFileSystemType.Ext3) => new()
    {
        Type = type,
        Label = "persistence",
        Uuid = FixedUuid,
        TimeProvider = new FakeTimeProvider(FixedTime),
    };

    private static SparseMemoryStream Format(long size, ExtFormatOptions options)
    {
        var stream = new SparseMemoryStream(size);
        ExtFormatter.Format(stream, options);
        return stream;
    }

    private static uint U32(SparseMemoryStream image, long offset) => BinaryPrimitives.ReadUInt32LittleEndian(image.ReadAt(offset, 4));

    private static ushort U16(SparseMemoryStream image, long offset) => BinaryPrimitives.ReadUInt16LittleEndian(image.ReadAt(offset, 2));

    [Fact]
    public void Format_WritesSuperblockFields()
    {
        using var image = Format(64 * MiB, Options() with { ReservedBlocksPercent = 5 });
        var sb = 1024;

        Assert.Equal(0xEF53, U16(image, sb + 0x38));
        Assert.Equal(1u, U32(image, sb + 0x4C));
        Assert.Equal(1u, U16(image, sb + 0x3A));
        Assert.Equal(65536u, U32(image, sb + 0x04));
        Assert.Equal(0u, U32(image, sb + 0x18));
        Assert.Equal(1u, U32(image, sb + 0x14));
        Assert.Equal(8192u, U32(image, sb + 0x20));
        Assert.Equal(3276u, U32(image, sb + 0x08));
        Assert.Equal(11u, U32(image, sb + 0x54));
        Assert.Equal(256, U16(image, sb + 0x58));
        Assert.Equal(8u, U32(image, sb + 0xE0));
        Assert.Equal((uint)FixedTime.ToUnixTimeSeconds(), U32(image, sb + 0x108));
        Assert.Equal(FixedUuid, new Guid(image.ReadAt(sb + 0x68, 16), bigEndian: true));
        Assert.Equal("persistence", System.Text.Encoding.ASCII.GetString(image.ReadAt(sb + 0x78, 11)));
    }

    [Theory]
    [InlineData(ExtFileSystemType.Ext2, 0x2u, 0x0u)]
    [InlineData(ExtFileSystemType.Ext3, 0x2u, 0x4u)]
    [InlineData(ExtFileSystemType.Ext4, 0x2u | 0x40u, 0x4u)]
    public void Format_SetsFeatureFlagsPerType(ExtFileSystemType type, uint incompat, uint journalFlag)
    {
        using var image = Format(64 * MiB, Options(type));

        Assert.Equal(incompat, U32(image, 1024 + 0x60));
        var compat = U32(image, 1024 + 0x5C);
        Assert.Equal(journalFlag, compat & 0x4);
        Assert.Equal(0x8u | 0x20u, compat & (0x8 | 0x20));
        var roCompat = U32(image, 1024 + 0x64);
        Assert.Equal(0x1u | 0x2u | 0x10u, roCompat & (0x1 | 0x2 | 0x10));
        Assert.Equal(type == ExtFileSystemType.Ext4, (roCompat & 0x8) != 0);
    }

    [Theory]
    [InlineData(8, 1024)]
    [InlineData(100, 1024)]
    [InlineData(128, 4096)]
    [InlineData(1024, 4096)]
    public void Format_ChoosesBlockSizeFromVolumeSize(long sizeMiB, int expected)
    {
        using var image = Format(sizeMiB * MiB, Options());

        Assert.Equal(expected, 1024 << (int)U32(image, 1024 + 0x18));
    }

    [Fact]
    public void Format_UsesExplicitBlockSize()
    {
        using var image = Format(64 * MiB, Options() with { BlockSize = 2048 });

        Assert.Equal(2048, 1024 << (int)U32(image, 1024 + 0x18));
        Assert.Equal(16384u, U32(image, 1024 + 0x20));
    }

    [Fact]
    public void Format_WithSameOptions_IsDeterministic()
    {
        using var first = Format(32 * MiB, Options(ExtFileSystemType.Ext4));
        using var second = Format(32 * MiB, Options(ExtFileSystemType.Ext4));

        Assert.True(first.ToArray().AsSpan().SequenceEqual(second.ToArray()));
    }

    [Fact]
    public void Format_WritesBackupSuperblocksOnlyInSparseGroups()
    {
        using var image = Format(2 * GiB, Options() with { BlockSize = 1024, LazyInitialization = true });
        var groups = (int)((U32(image, 1024 + 0x04) - 1 + 8191) / 8192);
        var withBackup = new HashSet<int> { 1, 3, 5, 7, 9, 25, 27, 49, 81, 125, 243, 343, 625, 729, 2187 };

        for (var group = 1; group < Math.Min(groups, 2200); group++)
        {
            var offset = (1L + group * 8192) * 1024;
            var magic = U16(image, offset + 0x38);
            Assert.Equal(withBackup.Contains(group), magic == 0xEF53);
            if (magic == 0xEF53)
            {
                Assert.Equal(group, U16(image, offset + 0x5A));
                Assert.Equal(0, U16(image, offset + 0x3A));
            }
        }
    }

    [Fact]
    public void Format_CopiesDescriptorTableToEveryBackupGroup()
    {
        using var image = Format(300 * MiB, Options() with { BlockSize = 1024 });
        var primary = image.ReadAt(2 * 1024, 2 * 1024);

        foreach (var group in new[] { 1, 3, 5, 7, 9, 25, 27 })
        {
            var copy = image.ReadAt((1 + group * 8192L + 1) * 1024, 2 * 1024);
            Assert.Equal(primary, copy);
        }
    }

    [Fact]
    public void Format_RecordsJournalBackupInSuperblock()
    {
        using var image = Format(64 * MiB, Options());
        var journal = InodeOffset(image, 8);

        for (var i = 0; i < 15; i++)
        {
            Assert.Equal(U32(image, journal + 0x28 + i * 4), U32(image, 1024 + 0x10C + i * 4));
        }

        Assert.Equal(U32(image, journal + 0x04), U32(image, 1024 + 0x10C + 64));
        Assert.Equal(1, image.ReadAt(1024 + 0xFD, 1)[0]);
    }

    [Fact]
    public void Format_DefaultJournalIsClampedToBounds()
    {
        using var small = Format(8 * MiB, Options());
        using var large = Format(2 * GiB, Options());

        Assert.Equal(1024L * 1024, JournalSize(small));
        Assert.Equal(32L * MiB, JournalSize(large));
    }

    [Fact]
    public void Format_UsesRequestedJournalSize()
    {
        using var image = Format(64 * MiB, Options() with { JournalSizeBytes = 4 * MiB });

        Assert.Equal(4 * MiB, JournalSize(image));
    }

    [Fact]
    public void Format_Ext2HasNoJournalInode()
    {
        using var image = Format(16 * MiB, Options(ExtFileSystemType.Ext2));

        Assert.Equal(0u, U32(image, 1024 + 0xE0));
        Assert.Equal(0, U16(image, InodeOffset(image, 8)));
    }

    [Fact]
    public void Format_CutsOffTrailingGroupWithoutRoomForMetadata()
    {
        using var image = Format(8 * MiB + 40 * 1024, Options(ExtFileSystemType.Ext2));

        Assert.Equal(8193u, U32(image, 1024 + 0x04));
    }

    [Fact]
    public void Format_ReturnsGeometry()
    {
        var stream = new SparseMemoryStream(2 * GiB);

        var result = ExtFormatter.Format(stream, Options());

        Assert.Equal(FixedUuid, result.Uuid);
        Assert.Equal(4096, result.BlockSize);
        Assert.Equal(524288, result.BlockCount);
        Assert.Equal(16, result.GroupCount);
        Assert.Equal(8192, result.InodesPerGroup);
        Assert.Equal(8192, result.JournalBlocks);
    }

    [Fact]
    public void Format_UsesSizeOptionInsteadOfStreamLength()
    {
        var stream = new SparseMemoryStream(64 * MiB);

        ExtFormatter.Format(stream, Options() with { SizeBytes = 16 * MiB });

        Assert.Equal(16384u, U32(stream, 1024 + 0x04));
    }

    [Fact]
    public void Format_ExtendsShortStream()
    {
        var stream = new SparseMemoryStream();

        ExtFormatter.Format(stream, Options() with { SizeBytes = 8 * MiB });

        Assert.Equal(8 * MiB, stream.Length);
    }

    [Fact]
    public void Format_CreatesRequestedRootFiles()
    {
        var content = "/ union\n"u8.ToArray();
        using var image = Format(16 * MiB, Options() with { RootFiles = [new ExtRootFile("persistence.conf", content)] });

        var inode = InodeOffset(image, 12);
        Assert.Equal(0x81A4, U16(image, inode));
        Assert.Equal((uint)content.Length, U32(image, inode + 4));
        Assert.Equal(1, U16(image, inode + 0x1A));
        var block = U32(image, inode + 0x28);
        Assert.Equal(content, image.ReadAt(block * 1024, content.Length));
    }

    [Fact]
    public void Format_RoundsTimestampsBeyond2038WithEpochBits()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2110, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using var image = Format(16 * MiB, Options() with { TimeProvider = clock });

        var inode = InodeOffset(image, 2);
        var seconds = clock.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal((uint)seconds, U32(image, inode + 0x10));
        Assert.Equal((seconds >> 32) & 3, U32(image, inode + 0x88) & 3);
    }

    [Fact]
    public void Format_Supports128ByteInodes()
    {
        using var image = Format(16 * MiB, Options(ExtFileSystemType.Ext4) with { InodeSize = 128 });

        Assert.Equal(128, U16(image, 1024 + 0x58));
        Assert.Equal(0u, U32(image, 1024 + 0x64) & 0x40);
        Assert.Equal(0, U16(image, 1024 + 0x15C));
    }

    [Fact]
    public void Format_Cancelled_Throws()
    {
        var stream = new SparseMemoryStream(16 * MiB);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ExtFormatter.Format(stream, Options() with { LazyInitialization = false }, cancellation.Token));
    }

    [Theory]
    [InlineData("abcdefghijklmnopq")]
    [InlineData("zwölf-Üüüüüü")]
    public void Format_LabelTooLong_Throws(string label)
    {
        var ex = Assert.Throws<BootrixException>(() => Format(16 * MiB, Options() with { Label = label }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(8192)]
    public void Format_UnsupportedBlockSize_Throws(int blockSize)
    {
        var ex = Assert.Throws<BootrixException>(() => Format(16 * MiB, Options() with { BlockSize = blockSize }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void Format_InvalidInodeSize_Throws()
    {
        var ex = Assert.Throws<BootrixException>(() => Format(16 * MiB, Options() with { InodeSize = 512 }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void Format_JournalSizeForExt2_Throws()
    {
        var options = Options(ExtFileSystemType.Ext2) with { JournalSizeBytes = 4 * MiB };

        var ex = Assert.Throws<BootrixException>(() => Format(16 * MiB, options));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(512 * 1024)]
    [InlineData(10 * MiB)]
    public void Format_JournalSizeOutOfRange_Throws(long journalBytes)
    {
        var ex = Assert.Throws<BootrixException>(() => Format(16 * MiB, Options() with { JournalSizeBytes = journalBytes }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(51.0)]
    [InlineData(double.NaN)]
    public void Format_ReservedPercentOutOfRange_Throws(double percent)
    {
        var ex = Assert.Throws<BootrixException>(() => Format(16 * MiB, Options() with { ReservedBlocksPercent = percent }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void Format_BytesPerInodeBelowBlockSize_Throws()
    {
        var ex = Assert.Throws<BootrixException>(() => Format(16 * MiB, Options() with { BlockSize = 4096, BytesPerInode = 1024 }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Theory]
    [InlineData(32 * 1024)]
    [InlineData(MiB)]
    public void Format_VolumeTooSmall_Throws(long size)
    {
        var ex = Assert.Throws<BootrixException>(() => Format(size, Options()));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
        Assert.Equal(2, ex.Arguments.Count);
    }

    [Fact]
    public void Format_UnreadableStream_Throws()
    {
        using var stream = new MemoryStream(new byte[1024], writable: false);

        Assert.Throws<ArgumentException>(() => ExtFormatter.Format(stream, Options()));
    }

    [Fact]
    public void Format_DuplicateRootFileName_Throws()
    {
        var options = Options() with { RootFiles = [new ExtRootFile("a", new byte[1]), new ExtRootFile("a", new byte[1])] };

        Assert.Throws<ArgumentException>(() => Format(16 * MiB, options));
    }

    private static long InodeOffset(SparseMemoryStream image, uint number)
    {
        var blockSize = 1024 << (int)U32(image, 1024 + 0x18);
        var perGroup = U32(image, 1024 + 0x28);
        var group = (number - 1) / perGroup;
        var table = U32(image, (long)(blockSize == 1024 ? 2 : 1) * blockSize + group * 32 + 8);
        return (long)table * blockSize + (number - 1) % perGroup * U16(image, 1024 + 0x58);
    }

    private static long JournalSize(SparseMemoryStream image) => U32(image, InodeOffset(image, 8) + 4);
}
