// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Ext;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>e2fsck -fn is the reference implementation: every image must come out without a single complaint.</summary>
public class ExtFormatterFsckTests
{
    private const long MiB = 1024 * 1024;

    private static readonly FakeTimeProvider Clock = new(new DateTimeOffset(2024, 3, 14, 9, 26, 53, TimeSpan.Zero));

    public static TheoryData<ExtFileSystemType, long, bool> Matrix()
    {
        var data = new TheoryData<ExtFileSystemType, long, bool>();
        foreach (var type in Enum.GetValues<ExtFileSystemType>())
        {
            foreach (var size in new[] { 8 * MiB, 64 * MiB, 1024 * MiB, 4096 * MiB })
            {
                data.Add(type, size, true);
                data.Add(type, size, false);
            }

            data.Add(type, 65536 * MiB, true);
        }

        return data;
    }

    private static ExtFormatOptions Options(ExtFileSystemType type, bool lazy) => new()
    {
        Type = type,
        Label = "persistence",
        LazyInitialization = lazy,
        TimeProvider = Clock,
        RootFiles = [new ExtRootFile("persistence.conf", "/ union\n"u8.ToArray())],
    };

    private static void FormatAndCheck(long size, ExtFormatOptions options)
    {
        using var image = new TempImage(size);
        ExtFormatter.Format(image.Stream, options);
        var path = image.Close();

        ExtAssert.Clean(path);
    }

    [ExtToolTheory]
    [MemberData(nameof(Matrix))]
    public void Format_ProducesImageThatFsckAccepts(ExtFileSystemType type, long size, bool lazy)
    {
        FormatAndCheck(size, Options(type, lazy) with { AssumeZeroedTarget = true });
    }

    [ExtToolTheory]
    [InlineData(ExtFileSystemType.Ext3, true)]
    [InlineData(ExtFileSystemType.Ext3, false)]
    [InlineData(ExtFileSystemType.Ext4, true)]
    [InlineData(ExtFileSystemType.Ext4, false)]
    public void Format_OfDirtyTarget_StillPassesFsck(ExtFileSystemType type, bool lazy)
    {
        using var image = new TempImage(64 * MiB);
        var junk = new byte[(int)image.Stream.Length];
        new Random(17).NextBytes(junk);
        image.Stream.Write(junk);

        ExtFormatter.Format(image.Stream, Options(type, lazy));
        ExtAssert.Clean(image.Close());
    }

    [ExtToolTheory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    public void Format_WithExplicitBlockSize_PassesFsck(int blockSize)
    {
        FormatAndCheck(300 * MiB, Options(ExtFileSystemType.Ext3, lazy: true) with { BlockSize = blockSize });
        FormatAndCheck(300 * MiB, Options(ExtFileSystemType.Ext4, lazy: false) with { BlockSize = blockSize });
    }

    [ExtToolTheory]
    [InlineData(ExtFileSystemType.Ext2)]
    [InlineData(ExtFileSystemType.Ext3)]
    [InlineData(ExtFileSystemType.Ext4)]
    public void Format_With128ByteInodes_PassesFsck(ExtFileSystemType type)
    {
        FormatAndCheck(128 * MiB, Options(type, lazy: true) with { InodeSize = 128 });
    }

    [ExtToolTheory]
    [InlineData(1024)]
    [InlineData(65536)]
    public void Format_WithCustomInodeDensity_PassesFsck(int bytesPerInode)
    {
        FormatAndCheck(512 * MiB, Options(ExtFileSystemType.Ext3, lazy: true) with { BytesPerInode = bytesPerInode, BlockSize = 1024 });
    }

    [ExtToolTheory]
    [InlineData(8 * MiB + 40 * 1024)]
    [InlineData(8 * MiB + 400 * 1024)]
    [InlineData(128 * MiB + 100 * 1024)]
    [InlineData(3 * 128 * MiB + 5 * 1024 * 1024 + 4096)]
    public void Format_WithUnalignedVolumeSize_PassesFsck(long size)
    {
        FormatAndCheck(size, Options(ExtFileSystemType.Ext3, lazy: true) with { BlockSize = size < 128 * MiB ? 1024 : 4096 });
    }

    [ExtToolFact]
    public void Format_WithReservedBlocksAndLargeJournal_PassesFsck()
    {
        FormatAndCheck(
            512 * MiB,
            Options(ExtFileSystemType.Ext4, lazy: true) with { ReservedBlocksPercent = 5, JournalSizeBytes = 100 * MiB });
    }

    // With uninit_bg e2fsck rebuilds the uninitialized groups from a backup and reports differences for mke2fs images
    // too, so the backup copies are only checked on fully initialized volumes.
    [ExtToolTheory]
    [InlineData(ExtFileSystemType.Ext2, 1)]
    [InlineData(ExtFileSystemType.Ext3, 3)]
    [InlineData(ExtFileSystemType.Ext4, 7)]
    public void Fsck_UsingBackupSuperblockAndDescriptors_PassesOnInitializedVolumes(ExtFileSystemType type, int group)
    {
        using var image = new TempImage(300 * MiB);
        ExtFormatter.Format(image.Stream, Options(type, lazy: false) with { BlockSize = 1024 });
        var path = image.Close();

        var fsck = ExtTools.Run("e2fsck", "-fn", "-B", "1024", "-b", (1 + group * 8192).ToString(System.Globalization.CultureInfo.InvariantCulture), path);

        Assert.True(fsck.ExitCode == 0, fsck.All);
    }

    [ExtToolFact]
    public void Format_OfLargeVolumeWithSmallBlocks_PassesFsck()
    {
        FormatAndCheck(16 * 1024 * MiB, Options(ExtFileSystemType.Ext3, lazy: true) with { BlockSize = 1024, AssumeZeroedTarget = true });
    }
}
