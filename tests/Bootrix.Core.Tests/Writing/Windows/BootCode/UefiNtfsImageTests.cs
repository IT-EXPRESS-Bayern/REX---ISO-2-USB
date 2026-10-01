// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Tests.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Windows;
using DiscUtils.Fat;
using DiscUtils.Streams;

namespace Bootrix.Core.Tests.Writing.Windows.BootCode;

public sealed class UefiNtfsImageTests : IDisposable
{
    // Hashes of assets/third-party/uefi-ntfs/uefi-ntfs.img and syslinux-mbr/mbr.bin as recorded in SOURCES.md.
    private const string ImageSha256 = "72683fa1250eeea772d3399277b434d4e55ba8dd0dc926e52d817e701fc2eb9e";
    private const string MbrSha256 = "4746f74bc9b9d3d579c41988a4a29bb7ac932ad1c70470ea779ea161eb799b64";

    private readonly TempImage? _image;

    public UefiNtfsImageTests()
    {
        _image = new TempImage(UefiNtfsImage.PartitionBytes);
    }

    public void Dispose() => _image?.Dispose();

    private string Write(byte[] content)
    {
        File.WriteAllBytes(_image!.Path, content);
        return _image.Path;
    }

    private static HashSet<string> ListFiles(byte[] image)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var fileSystem = new FatFileSystem(stream, Ownership.None);
        return [.. fileSystem.GetFiles(string.Empty, "*", SearchOption.AllDirectories).Select(path => path.Replace('\\', '/').Trim('/').ToLowerInvariant())];
    }

    private static byte[] ReadFile(byte[] image, string path)
    {
        using var stream = new MemoryStream(image, writable: false);
        using var fileSystem = new FatFileSystem(stream, Ownership.None);
        using var file = fileSystem.OpenFile(path, FileMode.Open, FileAccess.Read);
        using var copy = new MemoryStream();
        file.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void TheEmbeddedAssets_AreTheFilesWhoseHashesSourcesMdRecords()
    {
        Assert.Equal(ImageSha256, Convert.ToHexStringLower(SHA256.HashData(UefiNtfsImage.ForSectorSize(512))));
        Assert.Equal(MbrSha256, Convert.ToHexStringLower(SHA256.HashData(WindowsMbr.Bootstrap())));
        Assert.Equal(UefiNtfsImage.PartitionBytes, UefiNtfsImage.ForSectorSize(512).Length);
    }

    [RequiresToolFact("fsck.vfat", "mdir")]
    public void TheShippedImage_IsAValidFatVolumeWithTheLoadersForEveryArchitecture()
    {
        var path = Write(UefiNtfsImage.ForSectorSize(512));

        FatVerifier.Fsck(path);
        var listing = ExternalTools.Run("mdir", "-/", "-i", path, "::").Output;
        foreach (var name in new[] { "bootx64", "bootia32", "bootaa64", "ntfs_x64", "exfat_x64" })
        {
            Assert.Contains(name, listing, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RequiresToolFact("fsck.vfat", "mdir", "mcopy")]
    public void OnFourKibSectors_TheSameFilesFormAValidVolume()
    {
        var image = UefiNtfsImage.ForSectorSize(4096);
        var path = Write(image);

        var report = FatVerifier.Fsck(path);

        Assert.Equal(UefiNtfsImage.PartitionBytes, image.Length);
        Assert.Contains("4096", ExternalTools.Run("fsck.vfat", "-n", "-v", path).Output, StringComparison.Ordinal);
        Assert.True(report.DataClusters > 0);
        Assert.Equal(ListFiles(UefiNtfsImage.ForSectorSize(512)), ListFiles(image));

        // The loader read back by mtools, an implementation that shares no code with the writer.
        var copy = Path.Combine(Path.GetTempPath(), "bootrix-bootx64-" + Guid.NewGuid().ToString("N") + ".efi");
        try
        {
            Assert.Equal(0, ExternalTools.Run("mcopy", "-i", path, "::EFI/BOOT/bootx64.efi", copy).ExitCode);
            Assert.Equal(ReadFile(UefiNtfsImage.ForSectorSize(512), "EFI/BOOT/bootx64.efi"), File.ReadAllBytes(copy));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    [Fact]
    public void OnFourKibSectors_TheFilesAreTheSignedOnesByteForByte()
    {
        var shipped = UefiNtfsImage.ForSectorSize(512);
        var rebuilt = UefiNtfsImage.ForSectorSize(4096);

        foreach (var file in ListFiles(shipped))
        {
            Assert.Equal(ReadFile(shipped, file), ReadFile(rebuilt, file));
        }
    }

    [Fact]
    public void OtherSectorSizes_AreNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => UefiNtfsImage.ForSectorSize(1024));
    }
}
