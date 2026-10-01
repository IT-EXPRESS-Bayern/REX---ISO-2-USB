// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Ext;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.FileSystems.Ext;

public class ExtVolumeWriterTests
{
    private const long MiB = 1024 * 1024;

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2024, 3, 14, 9, 26, 53, TimeSpan.Zero));

    private static byte[] RandomBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    // Dumps through debugfs into a file so binary content survives; returns its SHA-256.
    private static string DumpHash(string image, string file)
    {
        var target = Path.Combine(Path.GetTempPath(), $"bootrix-dump-{Guid.NewGuid():N}");
        try
        {
            var result = ExtTools.Run("debugfs", "-R", $"dump {file} {target}", image);
            Assert.True(result.ExitCode == 0, result.All);
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [ExtToolTheory]
    [InlineData(ExtFileSystemType.Ext2)]
    [InlineData(ExtFileSystemType.Ext3)]
    [InlineData(ExtFileSystemType.Ext4)]
    public void Format_WithRootFiles_CreatesReadableFilesThatFsckAccepts(ExtFileSystemType type)
    {
        var small = RandomBytes(100, 1);
        var multiBlock = RandomBytes(300_000, 2);
        using var image = new TempImage(64 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions
        {
            Type = type,
            Label = "persistence",
            TimeProvider = Clock,
            RootFiles =
            [
                new ExtRootFile("persistence.conf", "/ union\n"u8.ToArray()),
                new ExtRootFile("small.bin", small),
                new ExtRootFile("multi.bin", multiBlock),
                new ExtRootFile("empty", ReadOnlyMemory<byte>.Empty),
            ],
        });
        var path = image.Close();

        ExtAssert.Clean(path);
        Assert.Equal("/ union\n", ExtTools.Debugfs(path, "cat /persistence.conf"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(small)), DumpHash(path, "/small.bin"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(multiBlock)), DumpHash(path, "/multi.bin"));
        Assert.Contains("empty", ExtTools.Debugfs(path, "ls /"));
        Assert.Contains("Size: 0", ExtTools.Debugfs(path, "stat /empty"));
    }

    [ExtToolTheory]
    [InlineData(ExtFileSystemType.Ext3, 1024)]
    [InlineData(ExtFileSystemType.Ext3, 4096)]
    [InlineData(ExtFileSystemType.Ext4, 1024)]
    [InlineData(ExtFileSystemType.Ext4, 4096)]
    public void AddRootFile_ToFormattedVolume_KeepsItConsistent(ExtFileSystemType type, int blockSize)
    {
        using var image = new TempImage(200 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { Type = type, BlockSize = blockSize, TimeProvider = Clock });
        var content = RandomBytes(5 * (int)MiB + 123, 3);

        var writer = ExtVolumeWriter.Open(image.Stream, Clock);
        writer.AddRootFile("persistence.conf", "/ union\n"u8.ToArray());
        writer.AddRootFile("casper-rw", content);
        var path = image.Close();

        ExtAssert.Clean(path);
        Assert.Equal("/ union\n", ExtTools.Debugfs(path, "cat /persistence.conf"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), DumpHash(path, "/casper-rw"));
    }

    [ExtToolFact]
    public void AddRootFile_NeedingDoubleIndirectBlocks_RoundTrips()
    {
        using var image = new TempImage(64 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { Type = ExtFileSystemType.Ext3, BlockSize = 1024, TimeProvider = Clock });
        var content = RandomBytes(2 * (int)MiB, 4);

        ExtVolumeWriter.Open(image.Stream, Clock).AddRootFile("two-meg", content);
        var path = image.Close();

        ExtAssert.Clean(path);
        Assert.Contains("(DIND):", ExtTools.Debugfs(path, "stat /two-meg"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), DumpHash(path, "/two-meg"));
    }

    [ExtToolFact]
    public void AddRootFile_NeedingTripleIndirectBlocks_RoundTrips()
    {
        using var image = new TempImage(160 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { Type = ExtFileSystemType.Ext2, BlockSize = 1024, TimeProvider = Clock });
        var content = RandomBytes(66 * (int)MiB, 5);

        ExtVolumeWriter.Open(image.Stream, Clock).AddRootFile("big", content);
        var path = image.Close();

        ExtAssert.Clean(path);
        Assert.Contains("(TIND):", ExtTools.Debugfs(path, "stat /big"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), DumpHash(path, "/big"));
    }

    [ExtToolFact]
    public void AddRootFile_SpanningGroupsOnExt4_UsesAnExtentIndexLeaf()
    {
        using var image = new TempImage(200 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { Type = ExtFileSystemType.Ext4, BlockSize = 1024, TimeProvider = Clock });
        var content = RandomBytes(60 * (int)MiB, 6);

        ExtVolumeWriter.Open(image.Stream, Clock).AddRootFile("spread", content);
        var path = image.Close();

        ExtAssert.Clean(path);
        var stat = ExtTools.Debugfs(path, "stat /spread");
        Assert.Contains("EXTENTS:", stat);
        Assert.Matches(@"\(ETB0\)", stat);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), DumpHash(path, "/spread"));
    }

    [ExtToolFact]
    public void AddRootFile_UpdatesTimestampsAndLinkCounts()
    {
        using var image = new TempImage(32 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { Type = ExtFileSystemType.Ext4, TimeProvider = new FakeTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)) });
        var later = new FakeTimeProvider(new DateTimeOffset(2025, 6, 16, 12, 30, 0, TimeSpan.Zero));

        ExtVolumeWriter.Open(image.Stream, later).AddRootFile(new ExtRootFile("run.sh", "#!/bin/sh\n"u8.ToArray()) { Permissions = 0b111_101_101 });
        var path = image.Close();

        ExtAssert.Clean(path);
        var file = ExtTools.Debugfs(path, "stat /run.sh");
        var root = ExtTools.Debugfs(path, "stat <2>");
        Assert.Contains("Mode:  0755", file);
        Assert.Contains("Links: 1", file);
        Assert.Contains("Mon Jun 16 12:30:00 2025", file);
        Assert.Contains("Mon Jun 16 12:30:00 2025", root.Split('\n').Single(line => line.StartsWith(" mtime", StringComparison.Ordinal)));
        Assert.Contains("Links: 3", root);
    }

    [ExtToolFact]
    public void AddRootFile_WithExistingName_ThrowsAndLeavesVolumeUntouched()
    {
        using var image = new TempImage(32 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { TimeProvider = Clock, RootFiles = [new ExtRootFile("a", new byte[10])] });
        var writer = ExtVolumeWriter.Open(image.Stream, Clock);

        Assert.Throws<ArgumentException>(() => writer.AddRootFile("a", new byte[5]));
        Assert.Throws<ArgumentException>(() => writer.AddRootFile("lost+found", new byte[5]));
        writer.AddRootFile("b", new byte[5]);
        var path = image.Close();

        ExtAssert.Clean(path);
        Assert.Contains("Size: 10", ExtTools.Debugfs(path, "stat /a"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("dir/file")]
    [InlineData("nul\0name")]
    public void AddRootFile_WithInvalidName_Throws(string name)
    {
        var stream = new SparseMemoryStream(16 * MiB);
        ExtFormatter.Format(stream, new ExtFormatOptions { TimeProvider = Clock });
        var writer = ExtVolumeWriter.Open(stream, Clock);

        Assert.Throws<ArgumentException>(() => writer.AddRootFile(name, new byte[1]));
    }

    [Fact]
    public void AddRootFile_WithTooLongName_Throws()
    {
        var stream = new SparseMemoryStream(16 * MiB);
        ExtFormatter.Format(stream, new ExtFormatOptions { TimeProvider = Clock });
        var writer = ExtVolumeWriter.Open(stream, Clock);

        Assert.Throws<ArgumentException>(() => writer.AddRootFile(new string('x', 256), new byte[1]));
        writer.AddRootFile(new string('x', 255), new byte[1]);
    }

    [ExtToolFact]
    public void AddRootFile_WithoutRoom_ThrowsInsufficientSpaceAndKeepsVolumeConsistent()
    {
        using var image = new TempImage(8 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { Type = ExtFileSystemType.Ext2, TimeProvider = Clock });
        var writer = ExtVolumeWriter.Open(image.Stream, Clock);

        var ex = Assert.Throws<BootrixException>(() => writer.AddRootFile("too-big", new byte[9 * (int)MiB]));
        writer.AddRootFile("fits", new byte[100_000]);
        var path = image.Close();

        Assert.Equal(ErrorCode.InsufficientSpace, ex.Code);
        ExtAssert.Clean(path);
    }

    [ExtToolFact]
    public void AddRootFile_UntilTheDirectoryBlockIsFull_ThrowsNotSupported()
    {
        using var image = new TempImage(16 * MiB);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions { Type = ExtFileSystemType.Ext2, BlockSize = 1024, TimeProvider = Clock });
        var writer = ExtVolumeWriter.Open(image.Stream, Clock);
        var added = 0;

        Assert.Throws<NotSupportedException>(() =>
        {
            for (; added < 1000; added++)
            {
                writer.AddRootFile($"entry-{added:D4}", new byte[1]);
            }
        });
        var path = image.Close();

        Assert.InRange(added, 30, 60);
        ExtAssert.Clean(path);
    }

    [Fact]
    public void Open_OnStreamWithoutFileSystem_Throws()
    {
        var stream = new SparseMemoryStream(8 * MiB);

        Assert.Throws<InvalidDataException>(() => ExtVolumeWriter.Open(stream));
    }

    [ExtToolTheory("mkfs.ext3")]
    [InlineData("-b 4096")]
    [InlineData("-b 1024")]
    [InlineData("-b 4096 -O ^resize_inode,uninit_bg")]
    [InlineData("-b 1024 -O ^resize_inode,uninit_bg -E lazy_itable_init=1")]
    [InlineData("-b 2048 -I 128 -O ^dir_index")]
    public void Open_OnMkfsExt3Image_AddsFileThatFsckAccepts(string mkfsOptions)
    {
        using var image = new TempImage(200 * MiB);
        image.Close();
        var result = ExtTools.Run("mkfs.ext3", ["-q", "-F", .. mkfsOptions.Split(' '), image.Path]);
        Assert.True(result.ExitCode == 0, result.All);
        var content = RandomBytes(1_000_000, 7);

        using (var stream = new FileStream(image.Path, FileMode.Open, FileAccess.ReadWrite))
        {
            var writer = ExtVolumeWriter.Open(stream, Clock);
            writer.AddRootFile("persistence.conf", "/ union\n"u8.ToArray());
            writer.AddRootFile("payload", content);
        }

        ExtAssert.Clean(image.Path);
        Assert.Equal("/ union\n", ExtTools.Debugfs(image.Path, "cat /persistence.conf"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), DumpHash(image.Path, "/payload"));
    }

    [ExtToolTheory("mkfs.ext4")]
    [InlineData("-O ^flex_bg,^metadata_csum,^64bit,^resize_inode")]
    [InlineData("-O ^flex_bg,^metadata_csum,^64bit,uninit_bg -b 1024")]
    public void Open_OnMkfsExt4WithoutFlexBg_AddsFileThatFsckAccepts(string mkfsOptions)
    {
        using var image = new TempImage(200 * MiB);
        image.Close();
        var result = ExtTools.Run("mkfs.ext4", ["-q", "-F", .. mkfsOptions.Split(' '), image.Path]);
        Assert.True(result.ExitCode == 0, result.All);
        var content = RandomBytes(2_000_000, 8);

        using (var stream = new FileStream(image.Path, FileMode.Open, FileAccess.ReadWrite))
        {
            ExtVolumeWriter.Open(stream, Clock).AddRootFile("payload", content);
        }

        ExtAssert.Clean(image.Path);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), DumpHash(image.Path, "/payload"));
    }

    [ExtToolFact("mkfs.ext4")]
    public void Open_OnDefaultMkfsExt4_IsRejectedBecauseOfItsFeatures()
    {
        using var image = new TempImage(64 * MiB);
        image.Close();
        Assert.Equal(0, ExtTools.Run("mkfs.ext4", "-q", "-F", image.Path).ExitCode);
        using var stream = new FileStream(image.Path, FileMode.Open, FileAccess.ReadWrite);

        Assert.Throws<NotSupportedException>(() => ExtVolumeWriter.Open(stream));
    }
}
