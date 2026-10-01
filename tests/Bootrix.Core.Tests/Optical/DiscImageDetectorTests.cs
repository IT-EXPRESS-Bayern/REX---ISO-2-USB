// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Tests.Optical.Support;

namespace Bootrix.Core.Tests.Optical;

public sealed class DiscImageDetectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-detect-" + Guid.NewGuid().ToString("N"));

    public DiscImageDetectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, byte[] data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static DiscImageKind Detect(byte[] data, string extension) => DiscImageDetector.Detect(new MemoryStream(data), extension);

    private static byte[] Noise(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void IsoWithPrimaryDescriptorIsAnIso()
    {
        Assert.Equal(DiscImageKind.Iso, Detect(OpticalTestData.DiscImage(100), ".iso"));
        Assert.Equal(DiscImageKind.Iso, Detect(OpticalTestData.DiscImage(100), ""));
    }

    [Fact]
    public void UdfOnlyImageIsRecognisedByItsVolumeRecognitionSequence()
    {
        var data = new byte[40 * 2048];
        data[16 * 2048] = 0;
        "BEA01"u8.CopyTo(data.AsSpan(16 * 2048 + 1));

        Assert.Equal(DiscImageKind.Iso, Detect(data, ".iso"));
    }

    [Fact]
    public void DescriptorAfterABootRecordIsStillFound()
    {
        var data = new byte[40 * 2048];
        OpticalTestData.WritePrimaryDescriptor(data.AsSpan(18 * 2048, 2048), 40);
        data[16 * 2048] = 0;
        "CD001"u8.CopyTo(data.AsSpan(16 * 2048 + 1));

        Assert.Equal(DiscImageKind.Iso, Detect(data, ".iso"));
    }

    [Fact]
    public void HybridIsoWithMbrIsStillAnIso()
    {
        var data = OpticalTestData.DiscImage(100);
        data[510] = 0x55;
        data[511] = 0xAA;

        Assert.Equal(DiscImageKind.Iso, Detect(data, ".img"));
    }

    [Fact]
    public void DiskImageWithPartitionTableIsNotOptical()
    {
        var data = new byte[1024 * 1024];
        data[510] = 0x55;
        data[511] = 0xAA;

        Assert.Equal(DiscImageKind.DiskImage, Detect(data, ".img"));
    }

    [Fact]
    public void AppleCdrIsAlwaysABurnableMaster()
    {
        Assert.Equal(DiscImageKind.Iso, Detect(Noise(200 * 1024), ".cdr"));
    }

    [Fact]
    public void HfsPlusImageWithoutIsoStructureIsAccepted()
    {
        var data = Noise(300 * 1024);
        data[1024] = (byte)'H';
        data[1025] = (byte)'+';

        Assert.Equal(DiscImageKind.Iso, Detect(data, ".iso"));
    }

    [Fact]
    public void NoiseWithIsoExtensionIsNotTrusted()
    {
        Assert.Equal(DiscImageKind.Unknown, Detect(Noise(200 * 1024), ".iso"));
    }

    [Fact]
    public void RawSectorsAreRecognisedByTheirSyncPattern()
    {
        var data = RawBin.FromIso(new byte[2048 * 20]);

        Assert.Equal(DiscImageKind.RawSectors, Detect(data, ".bin"));
    }

    [Fact]
    public void DmgIsRecognisedByItsKolyTrailer()
    {
        var data = Noise(8192);
        "koly"u8.CopyTo(data.AsSpan(data.Length - 512));

        Assert.Equal(DiscImageKind.Dmg, Detect(data, ".dmg"));
        Assert.Equal(DiscImageKind.Dmg, Detect(data, ""));
    }

    [Fact]
    public void NeroFootersOfBothGenerationsAreRecognised()
    {
        var modern = Noise(4096);
        "NER5"u8.CopyTo(modern.AsSpan(modern.Length - 12));
        var legacy = Noise(4096, 2);
        "NERO"u8.CopyTo(legacy.AsSpan(legacy.Length - 8));

        Assert.Equal(DiscImageKind.Nero, Detect(modern, ".nrg"));
        Assert.Equal(DiscImageKind.Nero, Detect(legacy, ".nrg"));
    }

    [Fact]
    public void AlcoholDescriptorIsRecognised()
    {
        var data = Noise(4096);
        "MEDIA DESCRIPTOR"u8.CopyTo(data);

        Assert.Equal(DiscImageKind.Alcohol, Detect(data, ".mds"));
    }

    [Fact]
    public void EmptyFileIsUnknown()
    {
        Assert.Equal(DiscImageKind.Unknown, Detect([], ".iso"));
    }

    [Theory]
    [InlineData("a.cue", DiscImageKind.BinCue)]
    [InlineData("a.dmg", DiscImageKind.Dmg)]
    [InlineData("a.nrg", DiscImageKind.Nero)]
    [InlineData("a.mdf", DiscImageKind.Alcohol)]
    [InlineData("a.mds", DiscImageKind.Alcohol)]
    [InlineData("a.ccd", DiscImageKind.CloneCd)]
    [InlineData("a.sub", DiscImageKind.CloneCd)]
    public void ExtensionsThatNameTheirFormat(string name, DiscImageKind kind)
    {
        Assert.Equal(kind, DiscImageDetector.Detect(Path.Combine(_dir, name)));
    }

    [Fact]
    public void BinNextToItsCueSheetIsBinCue()
    {
        var bin = Write("disc.bin", RawBin.FromIso(new byte[2048 * 20]));
        File.WriteAllText(Path.Combine(_dir, "disc.cue"), "x");

        Assert.Equal(DiscImageKind.BinCue, DiscImageDetector.Detect(bin));
    }

    [Fact]
    public void ImgNextToCcdIsCloneCd()
    {
        var img = Write("disc.img", Noise(4096));
        File.WriteAllText(Path.Combine(_dir, "disc.ccd"), "[CloneCD]");

        Assert.Equal(DiscImageKind.CloneCd, DiscImageDetector.Detect(img));
    }

    [Fact]
    public void ImgWithoutSidecarsIsJudgedByContent()
    {
        Assert.Equal(DiscImageKind.Iso, DiscImageDetector.Detect(Write("a.img", OpticalTestData.DiscImage(64))));
    }

    [Fact]
    public void OpenGivesAStreamOfTheWholeFile()
    {
        var data = OpticalTestData.DiscImage(64);
        var path = Write("a.iso", data);

        var source = DiscImageDetector.Open(path);

        Assert.Equal(DiscImageKind.Iso, source.Kind);
        Assert.Equal(Path.GetFullPath(path), source.FilePath);
        Assert.Equal(data.Length, source.LengthBytes);
        Assert.Equal(64, source.SectorCount);
        using var stream = source.OpenStream();
        var copy = new byte[data.Length];
        stream.ReadExactly(copy);
        Assert.Equal(data, copy);
        Assert.Equal("a.iso", source.DisplayName);
    }

    [Fact]
    public void StreamsAreIndependentForMultipleDrives()
    {
        var path = Write("a.iso", OpticalTestData.DiscImage(64));
        var source = DiscImageDetector.Open(path);

        using var first = source.OpenStream();
        using var second = source.OpenStream();
        first.Seek(1000, SeekOrigin.Begin);

        Assert.Equal(0, second.Position);
    }

    [Theory]
    [InlineData("disk.img")]
    [InlineData("noise.iso")]
    [InlineData("a.dmg")]
    [InlineData("a.nrg")]
    [InlineData("a.mdf")]
    public void ImagesThatCannotBeBurnedAreExplained(string name)
    {
        var data = name == "disk.img" ? DiskImage() : Noise(4096);
        var path = Write(name, data);

        var ex = Assert.Throws<BootrixException>(() => DiscImageDetector.Open(path));

        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);
        Assert.NotEmpty(ex.Arguments);
    }

    private static byte[] DiskImage()
    {
        var data = new byte[1024 * 1024];
        data[510] = 0x55;
        data[511] = 0xAA;
        return data;
    }

    [Fact]
    public void PaddedImageStreamRoundsUpToWholeSectors()
    {
        var data = Noise(5000);
        var source = new DiscImageSource("x.img", DiscImageKind.Iso, data.Length, () => new MemoryStream(data));

        Assert.Equal(3, source.SectorCount);
        Assert.Equal(3 * 2048, source.PaddedLengthBytes);
        using var padded = source.OpenPadded();
        Assert.Equal(3 * 2048, padded.Length);
        var all = new byte[3 * 2048];
        padded.ReadExactly(all);
        Assert.Equal(data, all[..5000]);
        Assert.All(all[5000..], b => Assert.Equal(0, b));
    }
}
