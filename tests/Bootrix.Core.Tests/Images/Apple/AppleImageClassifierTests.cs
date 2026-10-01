// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Apple;
using Bootrix.Core.Tests.Images.Udif;
using static Bootrix.Core.Tests.Images.Apple.AppleTestImages;

namespace Bootrix.Core.Tests.Images.Apple;

public class AppleImageClassifierTests
{
    private const int Size = 4 * 1024 * 1024;

    private static AppleImageInfo Classify(byte[] image) => AppleImageClassifier.Classify(new MemoryStream(image));

    [Fact]
    public void BareHfsPlusVolume_IsMacOnlyAndNeedsAPartitionTable()
    {
        var image = new byte[Size];
        WriteHfsPlusHeader(image);

        var info = Classify(image);

        Assert.Equal(AppleImageKind.HfsPlusVolume, info.Kind);
        Assert.Equal(AppleImageScheme.None, info.Scheme);
        Assert.Equal(AppleFileSystem.HfsPlus, info.FileSystem);
        Assert.True(info.IsMacOnly);
        Assert.False(info.IsBootable);
        Assert.Contains(AppleImageHint.MacOnly, info.Hints);
        Assert.Contains(AppleImageHint.BareVolume, info.Hints);
        Assert.Contains(AppleImageHint.DataVolumeOnly, info.Hints);
        Assert.DoesNotContain(AppleImageHint.T2Restriction, info.Hints);
        Assert.Equal(Size, info.VolumeSize);
    }

    [Fact]
    public void BareHfsPlusVolumeWithBlessedFolder_IsABootableMacVolume()
    {
        var image = new byte[Size];
        WriteHfsPlusHeader(image, blessedFolder: 2);

        var info = Classify(image);

        Assert.True(info.IsBootable);
        Assert.Contains(AppleImageHint.BootableMacVolume, info.Hints);
        Assert.Contains(AppleImageHint.T2Restriction, info.Hints);
        Assert.Contains(AppleImageHint.AppleSiliconUnsupported, info.Hints);
        Assert.DoesNotContain(AppleImageHint.DataVolumeOnly, info.Hints);
    }

    [Fact]
    public void CaseSensitiveHfsPlus_IsHfsX()
    {
        var image = new byte[Size];
        WriteHfsPlusHeader(image, caseSensitive: true);

        Assert.Equal(AppleFileSystem.HfsX, Classify(image).FileSystem);
    }

    [Fact]
    public void ClassicHfsVolume_IsFlaggedAsLegacy()
    {
        var image = new byte[Size];
        WriteHfsHeader(image, "Old Mac");

        var info = Classify(image);

        Assert.Equal(AppleImageKind.HfsVolume, info.Kind);
        Assert.Equal(AppleFileSystem.Hfs, info.FileSystem);
        Assert.Contains(AppleImageHint.ClassicHfs, info.Hints);
        Assert.DoesNotContain(AppleImageHint.T2Restriction, info.Hints);
    }

    [Fact]
    public void HfsWrapperAroundHfsPlus_IsSeenAsHfsPlus()
    {
        var image = new byte[Size];
        WriteHfsHeader(image, "Wrapper", embedHfsPlus: true);
        WriteHfsPlusHeader(image.AsSpan(8 * 512 + 2 * 1024), blessedFolder: 5);

        var info = Classify(image);

        Assert.Equal(AppleFileSystem.HfsPlus, info.FileSystem);
        Assert.True(info.IsBootable);
        Assert.DoesNotContain(AppleImageHint.ClassicHfs, info.Hints);
    }

    [Fact]
    public void ApfsContainer_IsRecognisedByItsSuperblock()
    {
        var image = new byte[Size];
        WriteApfsSuperblock(image, 4096, 1000);

        var info = Classify(image);

        Assert.Equal(AppleImageKind.ApfsContainer, info.Kind);
        Assert.Equal(AppleFileSystem.Apfs, info.FileSystem);
        Assert.Contains(AppleImageHint.ApfsContainer, info.Hints);
        Assert.Contains(AppleImageHint.BareVolume, info.Hints);
    }

    [Theory]
    [InlineData(1000u)]
    [InlineData(3000u)]
    [InlineData(2048u)]
    [InlineData(131072u)]
    public void ApfsSuperblockWithImplausibleBlockSize_IsNotAnApfsContainer(uint blockSize)
    {
        var image = new byte[Size];
        WriteApfsSuperblock(image, blockSize);

        Assert.Equal(AppleImageKind.NotApple, Classify(image).Kind);
    }

    [Theory]
    [InlineData(4096u)]
    [InlineData(8192u)]
    [InlineData(65536u)]
    public void ApfsSuperblockWithValidBlockSize_IsAnApfsContainer(uint blockSize)
    {
        var image = new byte[Size];
        WriteApfsSuperblock(image, blockSize);

        Assert.Equal(AppleImageKind.ApfsContainer, Classify(image).Kind);
    }

    [Fact]
    public void ApmDisk_WithOnlyApplePartitions_IsMacOnly()
    {
        var image = new byte[Size];
        WriteApm(image, 512,
            ("Apple", "Apple_partition_map", 1, 63),
            ("Macintosh HD", "Apple_HFS", 64, 8000),
            ("", "Apple_Free", 8064, 128));
        WriteHfsPlusHeader(image.AsSpan(64 * 512), blessedFolder: 7);

        var info = Classify(image);

        Assert.Equal(AppleImageKind.ApplePartitionMap, info.Kind);
        Assert.Equal(AppleImageScheme.Apm, info.Scheme);
        Assert.True(info.IsMacOnly);
        Assert.True(info.IsBootable);
        Assert.Equal(3, info.Partitions.Count);
        var hfs = info.Partitions[1];
        Assert.Equal(AppleFileSystem.HfsPlus, hfs.FileSystem);
        Assert.True(hfs.IsBlessed);
        Assert.Equal(64 * 512, hfs.Offset);
        Assert.Contains(AppleImageHint.MacOnly, info.Hints);
    }

    [Fact]
    public void ApmDisk_WithAPcPartition_IsNotMacOnly()
    {
        var image = new byte[Size];
        WriteApm(image, 512,
            ("Apple", "Apple_partition_map", 1, 63),
            ("Macintosh HD", "Apple_HFS", 64, 4000),
            ("Windows", "Windows_FAT_32", 4064, 4000));

        var info = Classify(image);

        Assert.Equal(AppleImageKind.ApplePartitionMap, info.Kind);
        Assert.False(info.IsMacOnly);
    }

    [Fact]
    public void GptDisk_WithHfsPlusAndRecovery_IsAMacDisk()
    {
        var image = new byte[Size];
        WriteGpt(image,
            (GptEsp, 40, 409),
            (GptHfsPlus, 410, 6000),
            (GptRecovery, 6001, 7000));
        WriteHfsPlusHeader(image.AsSpan(410 * 512), blessedFolder: 2);

        var info = Classify(image);

        Assert.Equal(AppleImageKind.GptMacDisk, info.Kind);
        Assert.Equal(AppleImageScheme.Gpt, info.Scheme);
        Assert.True(info.IsMacOnly);
        Assert.True(info.IsBootable);
        Assert.Equal(AppleFileSystem.HfsPlus, info.FileSystem);
        Assert.Equal(3, info.Partitions.Count);
        Assert.Equal("EFI System", info.Partitions[0].Type);
        Assert.Equal("Apple HFS+", info.Partitions[1].Type);
    }

    [Fact]
    public void GptDisk_WithAppleAndWindowsPartitions_IsNotMacOnly()
    {
        var image = new byte[Size];
        WriteGpt(image, (GptHfsPlus, 40, 4000), (GptMicrosoftBasic, 4001, 7000));
        WriteHfsPlusHeader(image.AsSpan(40 * 512));

        var info = Classify(image);

        Assert.Equal(AppleImageKind.GptMacDisk, info.Kind);
        Assert.False(info.IsMacOnly);
    }

    [Fact]
    public void GptDisk_WithPcBootCodeInTheProtectiveMbr_IsNotMacOnly()
    {
        var image = new byte[Size];
        WriteMbr(image, (0xEE, 1, 8000));
        image[0] = 0xEB;
        image[1] = 0x58;
        WriteGpt(image, (GptApfs, 40, 4000));
        WriteApfsSuperblock(image.AsSpan(40 * 512));

        var info = Classify(image);

        Assert.Equal(AppleImageKind.GptMacDisk, info.Kind);
        Assert.False(info.IsMacOnly);
    }

    [Fact]
    public void GptDisk_WithoutAppleTypes_IsNotAnAppleImage()
    {
        var image = new byte[Size];
        WriteGpt(image, (GptEsp, 40, 400), (GptLinux, 401, 7000));

        var info = Classify(image);

        Assert.Equal(AppleImageKind.NotApple, info.Kind);
        Assert.Equal(AppleImageScheme.Gpt, info.Scheme);
        Assert.False(info.IsMacOnly);
        Assert.Empty(info.Hints);
    }

    [Fact]
    public void GptWithApfsAndPrebootStyleRecovery_IsBootableWithoutReadingApfs()
    {
        var image = new byte[Size];
        WriteGpt(image, (GptApfs, 40, 4000), (GptRecovery, 4001, 7000));
        WriteApfsSuperblock(image.AsSpan(40 * 512));

        var info = Classify(image);

        Assert.True(info.IsBootable);
        Assert.Equal(AppleFileSystem.Apfs, info.FileSystem);
        Assert.Contains(AppleImageHint.ApfsContainer, info.Hints);
    }

    [Fact]
    public void MbrDisk_WithHfsPartition_IsAMacDisk()
    {
        var image = new byte[Size];
        WriteMbr(image, (0xAF, 64, 6000));
        WriteHfsPlusHeader(image.AsSpan(64 * 512), blessedFolder: 2);

        var info = Classify(image);

        Assert.Equal(AppleImageKind.MbrMacDisk, info.Kind);
        Assert.Equal(AppleImageScheme.Mbr, info.Scheme);
        Assert.True(info.IsMacOnly);
        Assert.True(info.IsBootable);
    }

    [Fact]
    public void PlainIso_IsNotAnAppleImage()
    {
        var image = new byte[Size];
        WriteIsoDescriptors(image, elTorito: true);

        var info = Classify(image);

        Assert.Equal(AppleImageKind.NotApple, info.Kind);
        Assert.True(info.HasIso9660);
        Assert.True(info.IsBootable);
        Assert.False(info.IsMacOnly);
    }

    [Fact]
    public void IsoWithApm_AndNoMbr_IsAHybridDisc()
    {
        var image = new byte[Size];
        WriteApm(image, 2048, ("Apple", "Apple_partition_map", 1, 4), ("Gap0", "ISO9660_data", 16, 16), ("hfs", "Apple_HFS", 32, 100));
        WriteIsoDescriptors(image, elTorito: false);
        WriteHfsPlusHeader(image.AsSpan(32 * 2048));

        var info = Classify(image);

        Assert.Equal(AppleImageKind.HybridDisc, info.Kind);
        Assert.False(info.IsMacOnly);
        Assert.Contains(AppleImageHint.HybridDisc, info.Hints);
    }

    [Fact]
    public void IsoWithApmAndMbr_IsALinuxIsohybrid_AndErAloneDoesNotMeanMac()
    {
        var image = new byte[Size];
        WriteApm(image, 2048, ("Apple", "Apple_partition_map", 1, 4), ("Gap0", "ISO9660_data", 16, 16), ("hfs", "Apple_HFS", 32, 100));
        WriteIsoDescriptors(image, elTorito: true);
        WriteMbr(image, (0x17, 0, 8000));

        var info = Classify(image);

        Assert.Equal(AppleImageKind.IsoHybridWithApm, info.Kind);
        Assert.False(info.IsMacOnly);
        Assert.True(info.IsBootable);
        Assert.Contains(AppleImageHint.IsoHybridWithApm, info.Hints);
        Assert.DoesNotContain(AppleImageHint.MacOnly, info.Hints);
    }

    [Fact]
    public void DriverDescriptorOnTopOfAnIso_WithoutPartitionEntries_IsNotMac()
    {
        var image = new byte[Size];
        image[0] = 0x45;
        image[1] = 0x52;
        image[3] = 0x08;
        WriteIsoDescriptors(image, elTorito: false);

        Assert.Equal(AppleImageKind.NotApple, Classify(image).Kind);
    }

    [Fact]
    public void PartitionBeyondTheEndOfTheImage_IsReported()
    {
        var image = new byte[Size];
        WriteApm(image, 512, ("Apple", "Apple_partition_map", 1, 63), ("x", "Apple_HFS", 64, 100_000));
        WriteHfsPlusHeader(image.AsSpan(64 * 512));

        var info = Classify(image);

        Assert.Contains(AppleImageHint.PartitionExceedsImage, info.Hints);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(2000)]
    [InlineData(200_000)]
    public void RandomOrShortData_IsNotAnAppleImage(int length)
    {
        var info = Classify(ImageTestData.Random(length, 12));

        Assert.Equal(AppleImageKind.NotApple, info.Kind);
        Assert.False(info.IsMacOnly);
        Assert.False(info.IsBootable);
    }

    [Fact]
    public void TruncatedMacImage_StillClassifiesWhatIsThere()
    {
        var image = new byte[Size];
        WriteGpt(image, (GptHfsPlus, 40, 7000));
        WriteHfsPlusHeader(image.AsSpan(40 * 512));

        var info = Classify(image[..(30 * 512)]);

        Assert.Equal(AppleImageKind.GptMacDisk, info.Kind);
        Assert.Contains(AppleImageHint.PartitionExceedsImage, info.Hints);
    }

    [Fact]
    public void EveryHint_HasGermanAndEnglishText()
    {
        foreach (var hint in Enum.GetValues<AppleImageHint>())
        {
            foreach (var culture in new[] { "de", "en" })
            {
                var localizer = new Bootrix.Core.Localization.Localizer { Culture = System.Globalization.CultureInfo.GetCultureInfo(culture) };
                Assert.True(localizer.Has(hint.ResourceKey()), $"{hint} missing for {culture}");
            }
        }
    }

    [Fact]
    public void DmgContainingAMacDisk_ReportsTheContainerAndTheDmgInfo()
    {
        var volume = new byte[DmgFixtures.VolumeSectors * 512];
        WriteGpt(volume, (GptHfsPlus, 34, 1500));
        WriteHfsPlusHeader(volume.AsSpan(34 * 512), blessedFolder: 2);
        var image = Udif.UdifBuilder.FromVolume(
            volume, DmgFixtures.Partitions, 64, (index, data) => DmgFixtures.Encode("zlib", index, data)).Build();
        var path = Path.Combine(Path.GetTempPath(), "bootrix-classify-" + Guid.NewGuid().ToString("N") + ".dmg");
        File.WriteAllBytes(path, image);
        try
        {
            var info = AppleImageClassifier.Classify(path);

            Assert.Equal(AppleImageContainer.Udif, info.Container);
            Assert.NotNull(info.Dmg);
            Assert.Equal(AppleImageKind.GptMacDisk, info.Kind);
            Assert.True(info.IsBootable);
            Assert.Equal(DmgFixtures.VolumeSectors * 512L, info.VolumeSize);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
