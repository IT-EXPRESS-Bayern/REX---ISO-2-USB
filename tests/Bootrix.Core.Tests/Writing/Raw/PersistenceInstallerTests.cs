// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.FileSystems.Ext;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Writing.Raw;

namespace Bootrix.Core.Tests.Writing.Raw;

/// <summary>
/// The persistence partition behind a raw-written image, checked by the tools that read such disks:
/// sfdisk and sgdisk for the tables, e2fsck, dumpe2fs and debugfs for the file system.
/// </summary>
public sealed class PersistenceInstallerTests : IDisposable
{
    private const long MiB = 1024 * 1024;
    private const long StickBytes = 96 * MiB;

    private readonly TestDirectory _dir = new("bootrix-persist");

    public void Dispose() => _dir.Dispose();

    /// <summary>A stick that has the image at its start; the rest is left as <paramref name="rest"/> (zero, or the leftovers of an earlier use).</summary>
    private string Stick(byte[] image, byte rest = 0, long size = StickBytes)
    {
        var path = _dir.File($"stick-{Guid.NewGuid():N}.img");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            file.SetLength(size);
            if (rest != 0)
            {
                file.Position = 0;
                file.Write(Enumerable.Repeat(rest, (int)Math.Min(size, 24 * MiB)).ToArray());
                file.Position = size - (4 * MiB);
                file.Write(Enumerable.Repeat(rest, (int)(4 * MiB)).ToArray());
            }

            file.Position = 0;
            file.Write(image);
        }

        return path;
    }

    private static PersistenceRequest Request(long imageLength, string label = "persistence", long length = 32 * MiB) => new()
    {
        StartBytes = (imageLength + MiB - 1) / MiB * MiB,
        LengthBytes = length,
        Label = label,
        Files = PersistenceFiles.For(label),
    };

    private static PersistenceResult Install(string stickPath, PersistenceRequest request, int sectorSize = 512)
    {
        using var device = new FileBlockDevice(stickPath, new FileInfo(stickPath).Length, sectorSize, create: false);
        return PersistenceInstaller.Install(device, request);
    }

    private void AssertCleanExt(string stickPath, PersistenceResult result, string expectedLabel)
    {
        var fs = HybridImages.Extract(_dir, stickPath, result.StartBytes, result.LengthBytes, "fs-" + Guid.NewGuid().ToString("N"));
        ExtAssert.Clean(fs);
        Assert.Contains($"Filesystem volume name:   {expectedLabel}", ExtTools.Run("dumpe2fs", "-h", fs).All, StringComparison.Ordinal);
    }

    [ToolFact("xorriso", "sfdisk", "e2fsck", "dumpe2fs", "debugfs")]
    public void DebianStyleMbr_GetsAThirdEntryAndAFileSystemThatFsckAccepts()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.DebianMbr);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image, rest: 0xFF);
        var before = HybridImages.Table(stick);

        var result = Install(stick, Request(image.Length));

        Assert.Equal(PersistenceTable.Mbr, result.Table);
        Assert.Equal(2, result.Slot);
        var after = HybridImages.Table(stick);
        Assert.Equal("dos", after.Label);
        Assert.Equal(before.Partitions, after.Partitions.Take(2));
        var added = after.Partitions[2];
        Assert.Equal("83", added.Type);
        Assert.Equal(result.StartBytes / 512, added.Start);
        Assert.Equal(result.LengthBytes / 512, added.Size);
        Assert.Equal(0, result.StartBytes % MiB);
        Assert.True(added.Start * 512 >= image.Length);
        AssertCleanExt(stick, result, "persistence");
        Assert.Equal("/ union\n", ExtTools.Debugfs(HybridImages.Extract(_dir, stick, result.StartBytes, result.LengthBytes, "conf-fs"), "cat /persistence.conf"));
    }

    [ToolFact("xorriso", "blkid")]
    public void Blkid_SeesAnExtVolumeWithTheLabelAtTheNewPartition()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.DebianMbr);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image, rest: 0xFF);

        var result = Install(stick, Request(image.Length));

        var probe = ReferenceTool.Run("blkid", ["-p", "-o", "export", "-O", result.StartBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), stick], null, null, null).StandardOutput;

        // With uninit_bg, which the formatter sets for fast formatting, libblkid reports the ext4 driver as the one to mount it.
        Assert.Matches("TYPE=ext[34]", probe);
        Assert.Contains("LABEL=persistence", probe, StringComparison.Ordinal);
    }

    [ToolFact("xorriso", "sfdisk")]
    public void Install_ChangesOnlyTheTableEntry_NotTheImage()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.DebianMbr);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image);

        var result = Install(stick, Request(image.Length));

        var written = new byte[image.Length];
        using (var file = File.OpenRead(stick))
        {
            file.ReadExactly(written);
        }

        for (var i = 0; i < image.Length; i++)
        {
            // Sector 0 may differ in the third table slot (bytes 478 to 493) and nowhere else.
            if (i is >= 478 and < 494)
            {
                continue;
            }

            Assert.True(image[i] == written[i], $"byte {i} of the image changed");
        }

        Assert.NotEqual(0, written[478 + 4]);
        Assert.True(result.StartBytes >= image.Length);
    }

    [ToolFact("xorriso", "sfdisk", "sgdisk", "e2fsck", "dumpe2fs")]
    public void UbuntuStyle_MbrWithGpt_ExtendsTheMbrAndLeavesTheGptAlone()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.UbuntuMbrAndGpt);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image);
        var sectorsBefore = new byte[34 * 512];
        using (var file = File.OpenRead(stick))
        {
            file.Position = 512;
            file.ReadExactly(sectorsBefore.AsSpan(0, 33 * 512));
        }

        var result = Install(stick, Request(image.Length, "writable"));

        Assert.Equal(PersistenceTable.Mbr, result.Table);
        var sectorsAfter = new byte[34 * 512];
        using (var file = File.OpenRead(stick))
        {
            file.Position = 512;
            file.ReadExactly(sectorsAfter.AsSpan(0, 33 * 512));
        }

        Assert.Equal(sectorsBefore, sectorsAfter);
        Assert.Equal(3, HybridImages.Table(stick).Partitions.Count);
        AssertCleanExt(stick, result, "writable");
    }

    [ToolFact("xorriso", "sfdisk", "e2fsck", "dumpe2fs")]
    public void ProtectiveLabelWithGpt_IsAnMbrCaseToo()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.ProtectiveLabelAndGpt);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image);

        var result = Install(stick, Request(image.Length));

        Assert.Equal(PersistenceTable.Mbr, result.Table);
        AssertCleanExt(stick, result, "persistence");
    }

    [ToolFact("xorriso", "sfdisk", "e2fsck", "dumpe2fs")]
    public void PlainIsohybrid_WithOneEntry_Works()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.Plain);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image);

        var result = Install(stick, Request(image.Length));

        Assert.Equal(1, result.Slot);
        AssertCleanExt(stick, result, "persistence");
    }

    [ToolFact("xorriso", "e2fsck", "dumpe2fs", "debugfs")]
    public void CasperLabel_GetsNoPersistenceConf()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.UbuntuMbrAndGpt);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image);

        var result = Install(stick, Request(image.Length, "writable"));

        var fs = HybridImages.Extract(_dir, stick, result.StartBytes, result.LengthBytes, "casper");
        Assert.DoesNotContain("persistence.conf", ExtTools.Debugfs(fs, "ls -l /"), StringComparison.Ordinal);
        Assert.Contains("lost+found", ExtTools.Debugfs(fs, "ls /"), StringComparison.Ordinal);
    }

    [ToolFact("sfdisk", "sgdisk", "e2fsck", "dumpe2fs")]
    public void ProtectiveMbrWithGpt_GetsAnEntryAndTheBackupMovesToTheEndOfTheDisk()
    {
        var imageBytes = 24 * MiB;
        var image = DiskImageBuilder.CreateGpt(
            _dir,
            "disk.img",
            imageBytes,
            new GptPartitionSpec(2048, 16384, "0700", "DATA"),
            new GptPartitionSpec(18432, 16384, "EF00", "ESP"));
        var bytes = File.ReadAllBytes(image);
        var stick = Stick(bytes);
        var firstBefore = ReferenceTool.Run("sgdisk", ["-i", "1", stick], null, null, null).StandardOutput;

        var result = Install(stick, Request(bytes.Length, "persistence", 40 * MiB));

        Assert.Equal(PersistenceTable.Gpt, result.Table);
        Assert.Equal(2, result.Slot);
        var verify = ReferenceTool.Run("sgdisk", ["--verify", stick], null, null, null);
        Assert.Contains("No problems found", verify.StandardOutput, StringComparison.Ordinal);
        var (label, partitions) = HybridImages.Table(stick);
        Assert.Equal("gpt", label);
        Assert.Equal(3, partitions.Count);
        Assert.Equal("persistence", partitions[2].Name);
        Assert.Equal("0FC63DAF-8483-4772-8E79-3D69D8477DE4", partitions[2].Type, ignoreCase: true);
        Assert.Equal(firstBefore, ReferenceTool.Run("sgdisk", ["-i", "1", stick], null, null, null).StandardOutput);
        Assert.Equal(StickBytes / 512 - 33, ParseLastUsable(stick) + 1);
        AssertCleanExt(stick, result, "persistence");
    }

    [ToolFact("sfdisk", "sgdisk")]
    public void ProtectiveMbr_IsWidenedToTheWholeDisk()
    {
        var bytes = File.ReadAllBytes(DiskImageBuilder.CreateGpt(_dir, "p.img", 24 * MiB, new GptPartitionSpec(2048, 8192, "0700", "A")));
        var stick = Stick(bytes);

        Install(stick, Request(bytes.Length));

        var sector = new byte[512];
        using (var file = File.OpenRead(stick))
        {
            file.ReadExactly(sector);
        }

        Assert.Equal(0xEE, sector[446 + 4]);
        Assert.Equal(StickBytes / 512 - 1, BitConverter.ToUInt32(sector, 446 + 12));
        Assert.Equal(1u, BitConverter.ToUInt32(sector, 446 + 8));
    }

    [ToolFact("sfdisk", "sgdisk")]
    public void GptWithGapsInItsSlots_KeepsTheNumbersOfExistingPartitions()
    {
        var path = DiskImageBuilder.Create(_dir, "gaps.img", 24 * MiB);
        ReferenceTool.Run("sgdisk", ["-n", "1:2048:6143", "-n", "4:8192:16383", "-t", "1:8300", "-t", "4:8300", path], null, null, null);
        var bytes = File.ReadAllBytes(path);
        var stick = Stick(bytes);

        var result = Install(stick, Request(bytes.Length));

        Assert.Equal(4, result.Slot);
        var info = ReferenceTool.Run("sgdisk", ["-p", stick], null, null, null).StandardOutput;
        Assert.Matches(@"\s+1\s+2048\s+6143", info);
        Assert.Matches(@"\s+4\s+8192\s+16383", info);
        Assert.Matches(@"\s+5\s+\d+\s+\d+\s+[\d.]+ MiB\s+8300\s+persistence", info);
    }

    [ToolFact("sfdisk")]
    public void FullMbr_IsRefused_AndNothingIsWritten()
    {
        var path = DiskImageBuilder.CreateMbr(
            _dir,
            "full.img",
            24 * MiB,
            new MbrPartitionSpec(2048, 4096, "83"),
            new MbrPartitionSpec(8192, 4096, "83"),
            new MbrPartitionSpec(16384, 4096, "83"),
            new MbrPartitionSpec(24576, 4096, "83"));
        var stick = Stick(File.ReadAllBytes(path), rest: 0xEE);
        var before = File.ReadAllBytes(stick);

        var error = Assert.Throws<BootrixException>(() => Install(stick, Request(24 * MiB)));

        Assert.Equal(ErrorCode.PersistenceLayoutUnsupported, error.Code);
        Assert.Equal(before, File.ReadAllBytes(stick));
    }

    [ToolFact("sgdisk")]
    public void HybridMbrNextToAProtectiveEntry_IsRefused()
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "hybrid.img", 24 * MiB, new GptPartitionSpec(2048, 8192, "0700", "A"));
        ReferenceTool.Run("sgdisk", ["--hybrid", "1", path], null, null, null);
        var stick = Stick(File.ReadAllBytes(path));
        var before = File.ReadAllBytes(stick);

        var error = Assert.Throws<BootrixException>(() => Install(stick, Request(24 * MiB)));

        Assert.Equal(ErrorCode.PersistenceLayoutUnsupported, error.Code);
        Assert.Equal(before, File.ReadAllBytes(stick));
    }

    [ToolFact("sgdisk")]
    public void DamagedGptEntryArray_IsRefused()
    {
        var bytes = File.ReadAllBytes(DiskImageBuilder.CreateGpt(_dir, "bad.img", 24 * MiB, new GptPartitionSpec(2048, 8192, "0700", "A")));
        bytes[1024 + 60] ^= 0x40;
        var stick = Stick(bytes);

        var error = Assert.Throws<BootrixException>(() => Install(stick, Request(24 * MiB)));

        Assert.Equal(ErrorCode.PersistenceLayoutUnsupported, error.Code);
    }

    [Fact]
    public void FourKnMedia_IsRefused()
    {
        var stick = Stick(new byte[512]);

        var error = Assert.Throws<BootrixException>(() => Install(stick, Request(MiB), sectorSize: 4096));

        Assert.Equal(ErrorCode.PersistenceLayoutUnsupported, error.Code);
    }

    [Fact]
    public void ImageWithoutAnMbrSignature_IsRefused()
    {
        var stick = Stick(new byte[2048]);

        var error = Assert.Throws<BootrixException>(() => Install(stick, Request(MiB)));

        Assert.Equal(ErrorCode.PersistenceLayoutUnsupported, error.Code);
    }

    [ToolFact("xorriso", "sfdisk")]
    public void NotEnoughRoomBehindTheImage_IsReportedAsTooSmall()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.DebianMbr);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image, size: 8 * MiB + 4 * MiB);

        var error = Assert.Throws<BootrixException>(() => Install(stick, Request(image.Length, length: 64 * MiB)));

        Assert.Equal(ErrorCode.PersistenceTooSmall, error.Code);
    }

    [ToolFact("xorriso", "sfdisk")]
    public void RequestLargerThanTheRoom_IsReducedToWhatFits()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.DebianMbr);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image, size: 40 * MiB);

        var result = Install(stick, Request(image.Length, length: 500 * MiB));

        Assert.True(result.StartBytes + result.LengthBytes <= 40 * MiB);
        Assert.True(result.LengthBytes >= 16 * MiB);
        Assert.Equal(0, result.LengthBytes % MiB);
    }

    [ToolFact("sfdisk")]
    public void StartIsMovedBehindWhatTheTableAlreadyOccupies()
    {
        // The table reaches to 40 MiB although the planner thought the image ends at 8 MiB.
        var path = DiskImageBuilder.CreateMbr(_dir, "far.img", 48 * MiB, new MbrPartitionSpec(2048, (38 * 2048) + 2048, "83"));
        var stick = Stick(File.ReadAllBytes(path), size: 160 * MiB);

        var result = Install(stick, Request(8 * MiB, length: 64 * MiB));

        Assert.True(result.StartBytes >= 40 * MiB);
        Assert.Equal(0, result.StartBytes % MiB);
        Assert.Equal(2, HybridImages.Table(stick).Partitions.Count);
    }

    [ToolFact("xorriso", "sfdisk")]
    public void FailureWhileFormatting_LeavesTheTableAsItWas()
    {
        var iso = HybridImages.BuildIso(_dir, HybridStyle.DebianMbr);
        var image = File.ReadAllBytes(iso);
        var stick = Stick(image);
        var before = new byte[2048];
        using (var file = File.OpenRead(stick))
        {
            file.ReadExactly(before);
        }

        using var inner = new FileBlockDevice(stick, new FileInfo(stick).Length, 512, create: false);
        using var failing = new FailingDevice(inner, failFrom: image.Length + (2 * MiB));
        Assert.Throws<IOException>(() => PersistenceInstaller.Install(failing, Request(image.Length)));

        var after = new byte[2048];
        using (var file = File.OpenRead(stick))
        {
            file.ReadExactly(after);
        }

        Assert.Equal(before, after);
        Assert.Equal(2, HybridImages.Table(stick).Partitions.Count);
    }

    private static long ParseLastUsable(string stick)
    {
        var output = ReferenceTool.Run("sfdisk", ["-d", stick], null, null, null).StandardOutput;
        var line = output.Split('\n').First(l => l.StartsWith("last-lba:", StringComparison.Ordinal));
        return long.Parse(line["last-lba:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FailingDevice(IBlockDevice inner, long failFrom) : IBlockDevice
    {
        public string Name => inner.Name;

        public int SectorSize => inner.SectorSize;

        public long Length => inner.Length;

        public int BufferAlignment => inner.BufferAlignment;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            if (offset >= failFrom)
            {
                throw new IOException("simulated failure");
            }

            inner.Write(offset, data);
        }

        public int Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

        public void Flush() => inner.Flush();

        public void Dispose()
        {
        }
    }
}
