// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Apple;
using Bootrix.Core.Tests.Images.Udif;

namespace Bootrix.Core.Tests.Images.Apple;

public sealed class AppleImageSourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-source-" + Guid.NewGuid().ToString("N"));

    public AppleImageSourceTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Theory]
    [InlineData(512, 1024)]
    [InlineData(2048, 2048)]
    [InlineData(4096, 4096)]
    public void RawFile_IsPaddedToTheSectorSize(int alignment, int expectedLength)
    {
        var content = ImageTestData.Random(1000, 1);
        var path = Write("disc.cdr", content);

        using var source = AppleImageSource.OpenForRawWrite(path, alignment);

        Assert.Equal(AppleImageContainer.Raw, source.Container);
        Assert.Equal(1000, source.ContentLength);
        Assert.Equal(expectedLength, source.Length);
        Assert.Equal(alignment, source.Alignment);
        var all = ReadAll(source.Stream);
        Assert.Equal(expectedLength, all.Length);
        Assert.True(all.AsSpan(0, 1000).SequenceEqual(content));
        Assert.All(all.Skip(1000), b => Assert.Equal(0, b));
    }

    [Fact]
    public void RawFileThatIsAlreadyAligned_IsNotExtended()
    {
        var path = Write("aligned.iso", ImageTestData.Random(4096, 2));

        using var source = AppleImageSource.OpenForRawWrite(path, 2048);

        Assert.Equal(4096, source.Length);
        Assert.Equal(source.ContentLength, source.Length);
    }

    [Fact]
    public void Dmg_IsDecodedAndPadded()
    {
        var volume = ImageTestData.Volume(1001, 5);
        var image = Udif.UdifBuilder.FromVolume(
            volume, [("disk image (Apple_HFS : 0)", 0, 1001)], 64, (index, data) => DmgFixtures.Encode("mixed", index, data)).Build();
        var path = Write("installer.dmg", image);

        using var source = AppleImageSource.OpenForRawWrite(path, 2048);

        Assert.Equal(AppleImageContainer.Udif, source.Container);
        Assert.NotNull(source.Dmg);
        Assert.Equal(1001 * 512, source.ContentLength);
        Assert.Equal(251 * 2048, source.Length);
        var all = ReadAll(source.Stream);
        Assert.True(all.AsSpan(0, volume.Length).SequenceEqual(volume));
        Assert.All(all.Skip(volume.Length), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Dmg_SeekingInThePaddedStream_ReturnsConsistentBytes()
    {
        var volume = ImageTestData.Volume(1001, 6);
        var image = Udif.UdifBuilder.FromVolume(
            volume, [("disk image (Apple_HFS : 0)", 0, 1001)], 32, (index, data) => DmgFixtures.Encode("zlib", index, data)).Build();
        using var source = AppleImageSource.OpenForRawWrite(Write("x.dmg", image), 2048);
        var random = new Random(3);
        var buffer = new byte[5000];

        for (var i = 0; i < 100; i++)
        {
            var offset = random.Next(0, (int)source.Length);
            source.Stream.Position = offset;
            var read = source.Stream.Read(buffer, 0, random.Next(1, buffer.Length));

            for (var k = 0; k < read; k++)
            {
                var expected = offset + k < volume.Length ? volume[offset + k] : (byte)0;
                Assert.Equal(expected, buffer[k]);
            }
        }
    }

    [Fact]
    public void SparseImage_IsDecoded()
    {
        var volume = SparseImageBuilder.SparseVolume(9, 4096, 4096, 1);
        var path = Write("disk.sparseimage", SparseImageBuilder.BuildImage(volume, 8));

        using var source = AppleImageSource.OpenForRawWrite(path);

        Assert.Equal(AppleImageContainer.SparseImage, source.Container);
        Assert.Null(source.Dmg);
        Assert.Equal(volume, ReadAll(source.Stream));
    }

    [Fact]
    public void SparseBundle_IsDecoded()
    {
        var volume = SparseImageBuilder.SparseVolume(9, 4096, 3000, 1);
        var bundle = Path.Combine(_directory, "disk.sparsebundle");
        SparseImageBuilder.BuildBundle(bundle, volume, 4096);

        using var source = AppleImageSource.OpenForRawWrite(bundle, 512);

        Assert.Equal(AppleImageContainer.SparseBundle, source.Container);
        Assert.Equal(volume.Length, source.ContentLength);
        Assert.Equal(8 * 4096 + 3072, source.Length);
        Assert.True(ReadAll(source.Stream).AsSpan(0, volume.Length).SequenceEqual(volume));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(8192)]
    public void UnsupportedAlignment_IsRejected(int alignment)
    {
        var path = Write("x.iso", new byte[2048]);

        Assert.Throws<ArgumentOutOfRangeException>(() => AppleImageSource.OpenForRawWrite(path, alignment));
    }

    [Fact]
    public void EncryptedImage_IsRejectedWithAClearError()
    {
        var content = new byte[4096];
        "encrcdsa"u8.CopyTo(content);
        var path = Write("secret.dmg", content);

        Assert.Equal(ErrorCode.ImageEncrypted, Assert.Throws<BootrixException>(() => AppleImageSource.OpenForRawWrite(path)).Code);
    }

    [Fact]
    public void MissingEmptyAndBundleLessPaths_AreReportedAsUnreadable()
    {
        var empty = Write("empty.img", []);
        var folder = Path.Combine(_directory, "folder");
        Directory.CreateDirectory(folder);

        foreach (var path in new[] { Path.Combine(_directory, "missing.dmg"), empty, folder })
        {
            Assert.Equal(ErrorCode.ImageUnreadable, Assert.Throws<BootrixException>(() => AppleImageSource.OpenForRawWrite(path)).Code);
        }
    }

    [Fact]
    public void Dispose_ReleasesTheFile()
    {
        var path = Write("release.cdr", new byte[4096]);

        AppleImageSource.OpenForRawWrite(path).Dispose();

        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
