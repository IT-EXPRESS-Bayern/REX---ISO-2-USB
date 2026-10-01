// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Ext;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>Checks the formatter's output through dumpe2fs and debugfs, which parse the file system independently of this code.</summary>
public class ExtFormatterToolTests
{
    private const long MiB = 1024 * 1024;
    private const string UuidText = "11111111-2222-3333-4444-555555555555";

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2024, 3, 14, 9, 26, 53, TimeSpan.Zero));

    private static TempImage Format(long size, ExtFormatOptions options)
    {
        var image = new TempImage(size);
        try
        {
            ExtFormatter.Format(image.Stream, options with { TimeProvider = Clock, Uuid = Guid.Parse(UuidText) });
            image.Close();
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    [Fact]
    public void SkipReason_NamesMissingTools()
    {
        var reason = ExtTools.SkipReason("no-such-tool-for-bootrix");

        Assert.NotNull(reason);
        Assert.Contains("no-such-tool-for-bootrix", reason);
    }

    [ExtToolTheory]
    [InlineData(ExtFileSystemType.Ext2, "ext_attr dir_index filetype sparse_super large_file uninit_bg")]
    [InlineData(ExtFileSystemType.Ext3, "has_journal ext_attr dir_index filetype sparse_super large_file uninit_bg")]
    [InlineData(ExtFileSystemType.Ext4, "has_journal ext_attr dir_index filetype extent sparse_super large_file huge_file uninit_bg dir_nlink extra_isize")]
    public void Dumpe2fs_ReportsFeaturesLabelAndIdentity(ExtFileSystemType type, string features)
    {
        using var image = Format(300 * MiB, new ExtFormatOptions { Type = type, Label = "casper-rw", BlockSize = 1024 });

        var dump = ExtDump.Read(image.Path);

        Assert.Equal(features.Split(' ').ToHashSet(), dump.Features);
        Assert.Equal("casper-rw", dump.Header["Filesystem volume name"]);
        Assert.Equal(UuidText, dump.Header["Filesystem UUID"]);
        Assert.Equal("0xEF53", dump.Header["Filesystem magic number"]);
        Assert.Equal("clean", dump.Header["Filesystem state"]);
        Assert.Equal("Continue", dump.Header["Errors behavior"]);
        Assert.Equal("0", dump.Header["Reserved block count"]);
        Assert.Equal("1024", dump.Header["Block size"]);
        Assert.Equal("307200", dump.Header["Block count"]);
        Assert.Equal("256", dump.Header["Inode size"]);
        Assert.Equal("11", dump.Header["First inode"]);
        Assert.Equal("user_xattr acl", dump.Header["Default mount options"]);
        Assert.Equal("half_md4", dump.Header["Default directory hash"]);
        Assert.Equal("signed_directory_hash", dump.Header["Filesystem flags"]);
        Assert.Equal(type != ExtFileSystemType.Ext2, dump.Header.ContainsKey("Journal inode"));
        Assert.Equal(38, dump.Groups.Count(line => line.StartsWith("Group ", StringComparison.Ordinal)));
    }

    [ExtToolFact]
    public void Dumpe2fs_ReportsJournalOfExt3()
    {
        using var image = Format(1024 * MiB, new ExtFormatOptions { Type = ExtFileSystemType.Ext3, JournalSizeBytes = 16 * MiB });

        var header = ExtDump.Read(image.Path).Header;

        Assert.Equal("8", header["Journal inode"]);
        Assert.Equal("inode blocks", header["Journal backup"]);
        Assert.Equal("(none)", header["Journal features"]);
        Assert.Equal("4096", header["Total journal blocks"]);
        Assert.Equal("0x00000001", header["Journal sequence"]);
        Assert.Equal("0", header["Journal start"]);
    }

    [ExtToolFact]
    public void Dumpe2fs_ReportsReservedBlocks()
    {
        using var image = Format(64 * MiB, new ExtFormatOptions { ReservedBlocksPercent = 5 });

        Assert.Equal("3276", ExtDump.Read(image.Path).Header["Reserved block count"]);
    }

    [ExtToolFact]
    public void Debugfs_ListsRootDirectoryAndPrintsTheFile()
    {
        using var image = Format(
            64 * MiB,
            new ExtFormatOptions { RootFiles = [new ExtRootFile("persistence.conf", "/ union\n"u8.ToArray())] });

        var listing = ExtTools.Debugfs(image.Path, "ls -l /");
        var root = ExtTools.Debugfs(image.Path, "stat <2>");
        var lostFound = ExtTools.Debugfs(image.Path, "stat <11>");

        Assert.Contains("lost+found", listing);
        Assert.Matches(@"100644 \(1\)\s+0\s+0\s+8\s+14-Mar-2024 09:26 persistence\.conf", listing);
        Assert.Equal("/ union\n", ExtTools.Debugfs(image.Path, "cat /persistence.conf"));
        Assert.Contains("Type: directory    Mode:  0755", root);
        Assert.Contains("Links: 3", root);
        Assert.Contains("User:     0   Group:     0", root);
        Assert.Contains("Type: directory    Mode:  0700", lostFound);
        Assert.Contains("Size: 16384", lostFound);
    }

    [ExtToolFact]
    public void Debugfs_ShowsExtentsForExt4Journal()
    {
        using var image = Format(1024 * MiB, new ExtFormatOptions { Type = ExtFileSystemType.Ext4 });

        var journal = ExtTools.Debugfs(image.Path, "stat <8>");

        Assert.Contains("Flags: 0x80000", journal);
        Assert.Contains("EXTENTS:", journal);
        Assert.Contains("Size of extra inode fields: 32", journal);
    }

    [ExtToolFact]
    public void Debugfs_ShowsBlockMapForExt3Journal()
    {
        using var image = Format(1024 * MiB, new ExtFormatOptions { Type = ExtFileSystemType.Ext3 });

        var journal = ExtTools.Debugfs(image.Path, "stat <8>");

        Assert.Contains("Flags: 0x0", journal);
        Assert.Contains("(IND):", journal);
        Assert.Contains("Type: regular    Mode:  0600", journal);
    }

    [ExtToolFact]
    public void Debugfs_ReportsCreationTimeFromTimeProvider()
    {
        using var image = Format(64 * MiB, new ExtFormatOptions { Type = ExtFileSystemType.Ext4 });

        var root = ExtTools.Debugfs(image.Path, "stat <2>");

        Assert.Contains("crtime: 0x", root);
        Assert.Contains("Thu Mar 14 09:26:53 2024", root);
    }
}
