// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Apple;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Tests.Images;
using Bootrix.Core.Tests.Images.Apple;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Images.Udif;
using Bootrix.Core.Writing.Raw;

namespace Bootrix.Core.Tests.Writing.Raw;

/// <summary>The decoding stream a raw write reads: system compressors as references, Apple containers from the fixture builders.</summary>
public sealed class ImageSourceOpenerTests : IDisposable
{
    private static readonly byte[] Payload = TestDirectory.Compressible(3 * 1024 * 1024 + 123);

    private readonly TestDirectory _dir = new("bootrix-source");

    public void Dispose() => _dir.Dispose();

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void AssertSame(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
    }

    private string Compressed(string tool, string[] arguments, string name)
    {
        var raw = _dir.Write("source.img", Payload);
        var target = _dir.File(name);
        ReferenceTool.Run(tool, arguments, _dir.Path, raw, target);
        return target;
    }

    [Fact]
    public void PlainFile_IsReadAsItIs()
    {
        var path = _dir.Write("plain.iso", Payload);

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(ImageSourceKind.File, source.Kind);
        Assert.Equal(Payload.Length, source.Length);
        Assert.Equal(CompressionFormat.None, source.Compression);
        AssertSame(Payload, ReadAll(source.Stream));
    }

    [Theory]
    [InlineData("gzip", "-c", "a.img.gz", CompressionFormat.GZip, false)]
    [InlineData("bzip2", "-c", "a.img.bz2", CompressionFormat.BZip2, false)]
    [InlineData("xz", "-c", "a.img.xz", CompressionFormat.Xz, true)]
    public void CompressedFile_IsDecodedWhileReading(string tool, string flag, string name, CompressionFormat format, bool lengthKnown)
    {
        if (!ReferenceTool.Exists(tool))
        {
            return;
        }

        var path = Compressed(tool, [flag], name);

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(ImageSourceKind.Compressed, source.Kind);
        Assert.Equal(format, source.Compression);
        Assert.Equal(lengthKnown ? Payload.Length : null, source.Length);
        AssertSame(Payload, ReadAll(source.Stream));
    }

    [ToolFact("zstd")]
    public void Zstd_FromAFile_KnowsTheDecodedLength()
    {
        var raw = _dir.Write("source.img", Payload);
        var target = _dir.File("a.img.zst");
        ReferenceTool.Run("zstd", ["-q", "-o", target, raw], null, null, null);

        using var source = ImageSourceOpener.Open(target);

        Assert.Equal(CompressionFormat.Zstd, source.Compression);
        Assert.Equal(Payload.Length, source.Length);
        AssertSame(Payload, ReadAll(source.Stream));
    }

    [ToolFact("lzma")]
    public void Lzma_IsAcceptedWithItsExtension()
    {
        var path = Compressed("lzma", ["-c"], "a.img.lzma");

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(CompressionFormat.Lzma, source.Compression);
        AssertSame(Payload, ReadAll(source.Stream));
    }

    [ToolFact("lzma")]
    public void Lzma_WithoutItsExtension_IsNotGuessed()
    {
        // The header of LZMA-alone is only a plausible-looking pattern, so a file without the name is left as it is.
        var path = Compressed("lzma", ["-c"], "a.img");

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(ImageSourceKind.File, source.Kind);
        Assert.Equal(new FileInfo(path).Length, source.Length);
    }

    [ToolFact("compress")]
    public void UnixCompress_IsDecoded()
    {
        var path = Compressed("compress", ["-c"], "a.img.Z");

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(CompressionFormat.Compress, source.Compression);
        AssertSame(Payload, ReadAll(source.Stream));
    }

    [ToolFact("gzip")]
    public void MagicBytesWinOverTheExtension()
    {
        var path = Compressed("gzip", ["-c"], "looks-like-an-iso.iso");

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(CompressionFormat.GZip, source.Compression);
        AssertSame(Payload, ReadAll(source.Stream));
    }

    [Fact]
    public void ExtensionWithoutMatchingContent_IsNotDecoded()
    {
        var path = _dir.Write("not-really.iso.gz", Payload);

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(ImageSourceKind.File, source.Kind);
        AssertSame(Payload, ReadAll(source.Stream));
    }

    [Fact]
    public void Zip_WithSeveralFiles_ReadsTheImageNotTheReadme()
    {
        var path = _dir.File("download.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "README.txt", "read me"u8.ToArray());
            Add(archive, "SHA256SUMS", new byte[100]);
            Add(archive, "disk/image.img", Payload);
        }

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(CompressionFormat.Zip, source.Compression);
        Assert.Equal("disk/image.img", source.ArchiveEntry);
        Assert.Equal(Payload.Length, source.Length);
        AssertSame(Payload, ReadAll(source.Stream));
    }

    [Fact]
    public void Zip_WithTwoImages_TakesTheNamedEntry()
    {
        var other = ImageTestData.Random(200_000, 3);
        var path = _dir.File("two.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "big.iso", Payload);
            Add(archive, "small.img", other);
        }

        using var source = ImageSourceOpener.Open(path, new ImageSourceOptions { ArchiveEntry = "small.img" });

        Assert.Equal("small.img", source.ArchiveEntry);
        AssertSame(other, ReadAll(source.Stream));
    }

    [Fact]
    public void Zip_WithAnUnknownEntry_Fails()
    {
        var path = _dir.File("one.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "a.img", new byte[1000]);
        }

        var error = Assert.Throws<BootrixException>(() => ImageSourceOpener.Open(path, new ImageSourceOptions { ArchiveEntry = "b.img" }));

        Assert.Equal(ErrorCode.ImageUnreadable, error.Code);
    }

    [ToolFact("xz")]
    public void TruncatedDownload_IsRefusedBeforeAnythingIsWritten()
    {
        var path = Compressed("xz", ["-c"], "cut.img.xz");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(file.Length / 2);
        }

        Assert.Throws<BootrixException>(() => ImageSourceOpener.Open(path));
    }

    [Fact]
    public void Dmg_IsUnpackedToItsVolume()
    {
        var path = _dir.Write("installer.dmg", DmgFixtures.Build("mixed"));

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(ImageSourceKind.AppleContainer, source.Kind);
        Assert.Equal(AppleImageContainer.Udif, source.AppleContainer);
        Assert.Equal(DmgFixtures.Volume.Length, source.Length);
        Assert.Equal(DmgFixtures.Volume, ReadAll(source.Stream));
    }

    [Fact]
    public void Dmg_WithAnotherExtension_IsStillRecognised()
    {
        var path = _dir.Write("renamed.bin", DmgFixtures.Build("zlib"));

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(ImageSourceKind.AppleContainer, source.Kind);
        Assert.Equal(DmgFixtures.Volume, ReadAll(source.Stream));
    }

    [Fact]
    public void RawFileNamedDmg_IsAPlainImage()
    {
        var content = ImageTestData.Random(600_000, 9);
        var path = _dir.Write("raw.dmg", content);

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(ImageSourceKind.File, source.Kind);
        Assert.Equal(content, ReadAll(source.Stream));
    }

    [Fact]
    public void SparseImage_IsUnpackedToItsVolume()
    {
        var volume = SparseImageBuilder.SparseVolume(12, 4096, 4096, 4);
        var path = _dir.Write("disk.sparseimage", SparseImageBuilder.BuildImage(volume, 8));

        using var source = ImageSourceOpener.Open(path);

        Assert.Equal(AppleImageContainer.SparseImage, source.AppleContainer);
        Assert.Equal(volume, ReadAll(source.Stream));
    }

    [Fact]
    public void SparseBundle_IsUnpackedWhenAllowed_AndRefusedOtherwise()
    {
        var volume = SparseImageBuilder.SparseVolume(10, 4096, 4096, 6);
        var bundle = _dir.File("disk.sparsebundle");
        SparseImageBuilder.BuildBundle(bundle, volume, 4096);

        using (var source = ImageSourceOpener.Open(bundle))
        {
            Assert.Equal(AppleImageContainer.SparseBundle, source.AppleContainer);
            Assert.Equal(volume, ReadAll(source.Stream));
        }

        var error = Assert.Throws<BootrixException>(() => ImageSourceOpener.Open(bundle, new ImageSourceOptions { AllowSparseBundle = false }));
        Assert.Equal(ErrorCode.ImageUnsupported, error.Code);
    }

    [Fact]
    public void EncryptedDmg_IsRefusedWithItsOwnError()
    {
        var content = new byte[8192];
        "encrcdsa"u8.CopyTo(content);
        var path = _dir.Write("secret.dmg", content);

        var error = Assert.Throws<BootrixException>(() => ImageSourceOpener.Open(path));

        Assert.Equal(ErrorCode.ImageEncrypted, error.Code);
    }

    [Fact]
    public void ForInspection_GivesTheRawFileForCompressedImagesAndTheVolumeForDmg()
    {
        var gzip = ReferenceTool.Exists("gzip") ? Compressed("gzip", ["-c"], "x.img.gz") : null;
        var dmg = _dir.Write("v.dmg", DmgFixtures.Build("raw"));

        if (gzip is not null)
        {
            using var file = ImageSourceOpener.OpenForInspection(gzip);
            Assert.True(file.Stream.CanSeek);
            Assert.Equal(CompressionFormat.GZip, file.Compression);
            Assert.Equal(new FileInfo(gzip).Length, file.Length);
        }

        using var volume = ImageSourceOpener.OpenForInspection(dmg);
        Assert.True(volume.Stream.CanSeek);
        Assert.Equal(DmgFixtures.Volume.Length, volume.Stream.Length);
        Assert.Equal(ImageSourceKind.AppleContainer, volume.Kind);
    }

    [Fact]
    public void BlockMapNextToTheImage_IsPickedUp_AndCanBeSwitchedOff()
    {
        var image = BmaptoolFixture.Image();
        var path = _dir.Write("fixture.img", image);
        File.WriteAllBytes(path + ".bmap", BmaptoolFixture.BlockMapBytes);

        using (var source = ImageSourceOpener.Open(path))
        {
            Assert.NotNull(source.BlockMap);
            Assert.Equal(9, source.BlockMap.MappedBlocksCount);
            Assert.False(source.FillBlockMapGaps);
        }

        using (var off = ImageSourceOpener.Open(path, new ImageSourceOptions { BlockMap = BlockMapUse.Off }))
        {
            Assert.Null(off.BlockMap);
        }

        using var fill = ImageSourceOpener.Open(path, new ImageSourceOptions { BlockMap = BlockMapUse.FillGaps });
        Assert.NotNull(fill.BlockMap);
        Assert.True(fill.FillBlockMapGaps);
    }

    [Fact]
    public void BlockMapOfAnotherSize_IsNotUsed()
    {
        var path = _dir.Write("short.img", new byte[BmaptoolFixture.ImageSize - 4096]);
        File.WriteAllBytes(path + ".bmap", BmaptoolFixture.BlockMapBytes);

        using var source = ImageSourceOpener.Open(path);

        Assert.Null(source.BlockMap);
        Assert.Contains("describes an image of", source.BlockMapSkipped, StringComparison.Ordinal);
    }

    [Fact]
    public void DamagedBlockMap_IsNotUsed_AndTheImageStillOpens()
    {
        var path = _dir.Write("broken.img", BmaptoolFixture.Image());
        File.WriteAllText(path + ".bmap", "<bmap version=\"2.0\"><BlockSize>nonsense</BlockSize></bmap>");

        using var source = ImageSourceOpener.Open(path);

        Assert.Null(source.BlockMap);
        Assert.NotNull(source.BlockMapSkipped);
    }

    [ToolFact("gzip")]
    public void BlockMapNextToACompressedImage_IsFoundWithoutTheCompressionSuffix()
    {
        var raw = _dir.Write("fixture.img", BmaptoolFixture.Image());
        var gz = _dir.File("fixture.img.gz");
        ReferenceTool.Run("gzip", ["-c", raw], null, null, gz);
        File.Delete(raw);
        File.WriteAllBytes(_dir.File("fixture.img.bmap"), BmaptoolFixture.BlockMapBytes);

        using var source = ImageSourceOpener.Open(gz);

        Assert.NotNull(source.BlockMap);
        Assert.Null(source.Length);
    }

    private static void Add(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.Fastest).Open();
        stream.Write(content);
    }
}
