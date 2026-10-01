// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using Bootrix.Core.FileSystems.Ext;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>Formats the same volume with mke2fs and with <see cref="ExtFormatter"/> and compares geometry and descriptors.</summary>
public class ExtReferenceComparisonTests
{
    private const long MiB = 1024 * 1024;
    private const string UuidText = "11111111-2222-3333-4444-555555555555";

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 3, 14, 9, 26, 53, TimeSpan.Zero));

    private static readonly string[] ComparedHeaderFields =
    [
        "Filesystem features", "Block count", "Block size", "Inode count", "Inodes per group", "Inode blocks per group",
        "Blocks per group", "Reserved block count", "Inode size", "First block", "First inode", "Reserved GDT blocks",
        "Default mount options", "Filesystem flags", "Default directory hash", "Required extra isize", "Desired extra isize",
        "Errors behavior", "Filesystem state", "Journal inode", "Journal backup", "Journal features",
    ];

    private static TempImage Reference(string tool, long size, params string[] arguments)
    {
        var image = new TempImage(size);
        image.Close();
        var result = ExtTools.Run(tool, ["-q", "-F", .. arguments, "-m", "0", "-L", "persistence", "-U", UuidText, image.Path]);
        Assert.True(result.ExitCode == 0, result.All);
        return image;
    }

    private static TempImage Ours(long size, ExtFormatOptions options)
    {
        var image = new TempImage(size);
        ExtFormatter.Format(image.Stream, options with { Label = "persistence", Uuid = Guid.Parse(UuidText), TimeProvider = Clock, AssumeZeroedTarget = true });
        image.Close();
        return image;
    }

    public static TheoryData<ExtFileSystemType, long, int, int> Cases() => new()
    {
        { ExtFileSystemType.Ext3, 8 * MiB, 1024, 4096 },
        { ExtFileSystemType.Ext3, 64 * MiB, 1024, 4096 },
        { ExtFileSystemType.Ext3, 300 * MiB, 1024, 4096 },
        { ExtFileSystemType.Ext3, 1024 * MiB, 4096, 16384 },
        { ExtFileSystemType.Ext3, 4096 * MiB, 4096, 16384 },
        { ExtFileSystemType.Ext3, 65536 * MiB, 4096, 16384 },
        { ExtFileSystemType.Ext4, 1024 * MiB, 4096, 16384 },
        { ExtFileSystemType.Ext4, 100 * MiB, 2048, 8192 },
        { ExtFileSystemType.Ext2, 512 * MiB, 4096, 16384 },
    };

    [ExtToolTheory("mkfs.ext2", "mkfs.ext3", "mkfs.ext4")]
    [MemberData(nameof(Cases))]
    public void Format_MatchesMkfsGeometry(ExtFileSystemType type, long size, int blockSize, int bytesPerInode)
    {
        var tool = type == ExtFileSystemType.Ext4 ? "mkfs.ext4" : type == ExtFileSystemType.Ext3 ? "mkfs.ext3" : "mkfs.ext2";
        // Everything the formatter does not implement is switched off so the remaining layout is directly comparable.
        var features = type == ExtFileSystemType.Ext4
            ? "^resize_inode,^flex_bg,^metadata_csum,^64bit,uninit_bg"
            : "^resize_inode,uninit_bg";
        using var reference = Reference(
            tool,
            size,
            "-b", blockSize.ToString(CultureInfo.InvariantCulture), "-I", "256", "-i", bytesPerInode.ToString(CultureInfo.InvariantCulture), "-O", features,
            "-E", "lazy_itable_init=1,lazy_journal_init=1");
        using var ours = Ours(size, new ExtFormatOptions { Type = type, BlockSize = blockSize, BytesPerInode = bytesPerInode });

        var expected = ExtDump.Read(reference.Path);
        var actual = ExtDump.Read(ours.Path);

        foreach (var field in ComparedHeaderFields.Where(field => type != ExtFileSystemType.Ext2 || !field.StartsWith("Journal", StringComparison.Ordinal)))
        {
            Assert.Equal(expected.Header.GetValueOrDefault(field), actual.Header.GetValueOrDefault(field));
        }

        Assert.Equal(expected.Groups, actual.Groups);
    }

    [ExtToolFact("mkfs.ext3")]
    public void GroupDescriptorChecksum_MatchesMkfsOutput()
    {
        using var reference = Reference("mkfs.ext3", 1024 * MiB, "-b", "4096", "-O", "^resize_inode,uninit_bg");
        using var stream = new FileStream(reference.Path, FileMode.Open, FileAccess.Read);
        var superblock = new byte[1024];
        stream.Position = 1024;
        stream.ReadExactly(superblock);
        var uuid = superblock.AsSpan(0x68, 16).ToArray();
        var groups = (int)(BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x04)) / BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(0x20)));
        var table = new byte[groups * ExtGroupDescriptor.Size];
        stream.Position = 4096;
        stream.ReadExactly(table);

        for (var group = 0; group < groups; group++)
        {
            var descriptor = ExtGroupDescriptor.Read(table.AsSpan(group * ExtGroupDescriptor.Size));
            Assert.Equal(descriptor.Checksum, descriptor.ComputeChecksum(uuid, group));
        }
    }

    [ExtToolFact("mkfs.ext3")]
    public void GroupFlags_FollowTheSameRulesAsMkfs()
    {
        using var reference = Reference("mkfs.ext3", 512 * MiB, "-b", "4096", "-O", "^resize_inode,uninit_bg", "-E", "lazy_itable_init=1");
        using var ours = Ours(512 * MiB, new ExtFormatOptions { BlockSize = 4096 });

        var expected = GroupFlags(reference.Path);
        var actual = GroupFlags(ours.Path);

        // Where mke2fs puts the journal decides which middle groups lose BLOCK_UNINIT, so only the rules that
        // do not depend on it are compared: unused inode tables, and the last group keeping its bitmap.
        Assert.Equal(expected.Select(flags => flags & ExtGroupDescriptor.FlagInodeUninit), actual.Select(flags => flags & ExtGroupDescriptor.FlagInodeUninit));
        Assert.Equal(0, expected[^1] & ExtGroupDescriptor.FlagBlockUninit);
        Assert.Equal(0, actual[^1] & ExtGroupDescriptor.FlagBlockUninit);
    }

    private static List<int> GroupFlags(string image)
    {
        using var stream = new FileStream(image, FileMode.Open, FileAccess.Read);
        stream.Position = 4096;
        var table = new byte[4 * ExtGroupDescriptor.Size];
        stream.ReadExactly(table);
        return Enumerable.Range(0, 4).Select(group => (int)ExtGroupDescriptor.Read(table.AsSpan(group * ExtGroupDescriptor.Size)).Flags).ToList();
    }
}
