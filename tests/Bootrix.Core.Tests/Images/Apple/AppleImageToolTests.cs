// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Tests.Images.Apple;

/// <summary>
/// Classification of images produced by independent tools (mkfs.hfsplus, parted, sgdisk, xorriso), so the
/// signature offsets are checked against someone else's writer and not only against this code's own idea of them.
/// </summary>
public sealed class AppleImageToolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-apple-" + Guid.NewGuid().ToString("N"));

    public AppleImageToolTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static void Run(string tool, params string[] arguments)
    {
        var (code, output) = ExternalTool.Run(ExternalTool.Find(tool)!, arguments);
        Assert.True(code == 0, $"{tool} failed: {output}");
    }

    private string MakeHfsPlus(string name, int megabytes, string volumeName = "TestVol")
    {
        var path = PathOf(name);
        File.WriteAllBytes(path, new byte[megabytes * 1024 * 1024]);
        Run("mkfs.hfsplus", "-v", volumeName, path);
        return path;
    }

    private static void CopyInto(string destination, string source, long offset)
    {
        using var target = new FileStream(destination, FileMode.Open, FileAccess.Write);
        target.Position = offset;
        using var data = File.OpenRead(source);
        data.CopyTo(target);
    }

    [RequiresToolFact("mkfs.hfsplus")]
    public void HfsPlusVolumeFromMkfs_IsRecognisedWithItsSize()
    {
        var path = MakeHfsPlus("vol.img", 8);

        var info = AppleImageClassifier.Classify(path);

        Assert.Equal(AppleImageKind.HfsPlusVolume, info.Kind);
        Assert.Equal(AppleFileSystem.HfsPlus, info.FileSystem);
        Assert.Equal(AppleImageContainer.Raw, info.Container);
        Assert.Equal(8 * 1024 * 1024, info.VolumeSize);
        Assert.Contains(AppleImageHint.BareVolume, info.Hints);
        Assert.Contains(AppleImageHint.DataVolumeOnly, info.Hints);
    }

    [RequiresToolFact("mkfs.hfs")]
    public void ClassicHfsVolumeFromMkfs_IsRecognised()
    {
        var path = PathOf("classic.img");
        File.WriteAllBytes(path, new byte[5 * 1024 * 1024]);
        Run("mkfs.hfs", "-v", "ClassicVol", path);

        var info = AppleImageClassifier.Classify(path);

        Assert.Equal(AppleImageKind.HfsVolume, info.Kind);
        Assert.Contains(AppleImageHint.ClassicHfs, info.Hints);
    }

    [RequiresToolFact("mkfs.hfsplus", "parted")]
    public void PartedApmDisk_WithHfsPlusInsideThePartition_ListsTheVolume()
    {
        var volume = MakeHfsPlus("inner.img", 8);
        var disk = PathOf("apm.img");
        File.WriteAllBytes(disk, new byte[24 * 1024 * 1024]);
        Run("parted", "-s", disk, "mklabel", "mac", "mkpart", "primary", "hfs+", "1MiB", "9MiB");
        CopyInto(disk, volume, 1024 * 1024);

        var info = AppleImageClassifier.Classify(disk);

        Assert.Equal(AppleImageKind.ApplePartitionMap, info.Kind);
        Assert.Equal(AppleImageScheme.Apm, info.Scheme);
        Assert.True(info.IsMacOnly);
        var hfs = Assert.Single(info.Partitions, p => p.Type == ApmPartitionTypes.Hfs);
        Assert.Equal(AppleFileSystem.HfsPlus, hfs.FileSystem);
        Assert.Equal(1024 * 1024, hfs.Offset);
    }

    [RequiresToolFact("mkfs.hfsplus", "sgdisk")]
    public void SgdiskGptDisk_WithAppleTypeGuids_IsAMacDisk()
    {
        var volume = MakeHfsPlus("inner.img", 8);
        var disk = PathOf("gpt.img");
        File.WriteAllBytes(disk, new byte[32 * 1024 * 1024]);
        Run("sgdisk", "-n", "1:2048:+8M", "-t", "1:AF00", "-n", "2:20480:+8M", "-t", "2:AF0A", disk);
        CopyInto(disk, volume, 2048L * 512);

        var info = AppleImageClassifier.Classify(disk);

        Assert.Equal(AppleImageKind.GptMacDisk, info.Kind);
        Assert.Equal(AppleImageScheme.Gpt, info.Scheme);
        Assert.Equal(2, info.Partitions.Count);
        Assert.Equal("Apple HFS+", info.Partitions[0].Type);
        Assert.Equal(AppleFileSystem.HfsPlus, info.Partitions[0].FileSystem);
        Assert.Equal(2048L * 512, info.Partitions[0].Offset);
        Assert.Equal("Apple APFS", info.Partitions[1].Type);
        Assert.Equal(AppleFileSystem.Unknown, info.Partitions[1].FileSystem);
        Assert.True(info.IsMacOnly);
    }

    [RequiresToolFact("sgdisk")]
    public void SgdiskGptDisk_WithLinuxAndEfiPartitions_IsNotApple()
    {
        var disk = PathOf("linux.img");
        File.WriteAllBytes(disk, new byte[32 * 1024 * 1024]);
        Run("sgdisk", "-n", "1:2048:+8M", "-t", "1:EF00", "-n", "2:20480:+8M", "-t", "2:8300", disk);

        var info = AppleImageClassifier.Classify(disk);

        Assert.Equal(AppleImageKind.NotApple, info.Kind);
        Assert.Equal(2, info.Partitions.Count);
        Assert.False(info.IsMacOnly);
    }

    [RequiresToolFact("mkfs.vfat")]
    public void FatVolume_IsNotApple()
    {
        var path = PathOf("fat.img");
        File.WriteAllBytes(path, new byte[16 * 1024 * 1024]);
        Run("mkfs.vfat", "-F", "32", path);

        var info = AppleImageClassifier.Classify(path);

        Assert.Equal(AppleImageKind.NotApple, info.Kind);
        Assert.Equal(AppleFileSystem.Unknown, info.FileSystem);
        Assert.False(info.IsMacOnly);
        Assert.Empty(info.Hints);
    }

    [RequiresToolFact("mkfs.ext4")]
    public void Ext4Volume_IsNotApple()
    {
        var path = PathOf("ext4.img");
        File.WriteAllBytes(path, new byte[16 * 1024 * 1024]);
        Run("mkfs.ext4", "-q", "-F", path);

        var info = AppleImageClassifier.Classify(path);

        Assert.Equal(AppleImageKind.NotApple, info.Kind);
        Assert.Empty(info.Hints);
    }

    [RequiresToolFact("xorriso")]
    public void XorrisoHybridWithHfsPlus_IsAHybridDisc()
    {
        var tree = PathOf("tree");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "hello");
        var iso = PathOf("hybrid.iso");
        Run("xorriso", "-as", "mkisofs", "-r", "-hfsplus", "-apm-block-size", "2048", "-o", iso, tree);

        var info = AppleImageClassifier.Classify(iso);

        Assert.True(info.HasIso9660);
        Assert.Equal(AppleImageKind.HybridDisc, info.Kind);
        Assert.Equal(AppleImageScheme.Apm, info.Scheme);
        Assert.Contains(info.Partitions, p => p.Type == ApmPartitionTypes.Hfs);
        Assert.False(info.IsMacOnly);
    }

    [RequiresToolFact("xorriso")]
    public void XorrisoIsoWithElToritoAndMbrSignature_IsALinuxIsohybridWithMacSupport()
    {
        var tree = PathOf("tree2");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "boot.bin"), new byte[2048]);
        var iso = PathOf("linux.iso");
        Run("xorriso", "-as", "mkisofs", "-r", "-hfsplus", "-apm-block-size", "2048", "-b", "boot.bin", "-no-emul-boot", "-boot-load-size", "4", "-o", iso, tree);

        // xorriso's isohybrid mode needs an isolinux image; the MBR signature it would add is set by hand.
        using (var stream = new FileStream(iso, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = 510;
            stream.Write([0x55, 0xAA]);
        }

        var info = AppleImageClassifier.Classify(iso);

        Assert.Equal(AppleImageKind.IsoHybridWithApm, info.Kind);
        Assert.True(info.IsBootable);
        Assert.Contains(AppleImageHint.IsoHybridWithApm, info.Hints);
    }

    [RequiresToolFact("xorriso")]
    public void XorrisoPlainIso_IsNotApple()
    {
        var tree = PathOf("tree3");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "a.txt"), "hello");
        var iso = PathOf("plain.iso");
        Run("xorriso", "-as", "mkisofs", "-r", "-o", iso, tree);

        var info = AppleImageClassifier.Classify(iso);

        Assert.Equal(AppleImageKind.NotApple, info.Kind);
        Assert.True(info.HasIso9660);
    }
}
