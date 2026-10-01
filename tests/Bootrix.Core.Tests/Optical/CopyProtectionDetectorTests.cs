// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Optical.Reading;
using Bootrix.Core.Tests.Optical.Support;

namespace Bootrix.Core.Tests.Optical;

public sealed class CopyProtectionDetectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-prot-" + Guid.NewGuid().ToString("N"));

    public CopyProtectionDetectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Builds a real ISO 9660 image with xorriso so the file system reader is tested against an independent writer.</summary>
    private byte[] BuildIso(params string[] folders)
    {
        var tree = Path.Combine(_dir, "tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "readme.txt"), "disc");
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(Path.Combine(tree, folder));
            File.WriteAllText(Path.Combine(tree, folder, "marker.inf"), "x");
        }

        var iso = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".iso");
        var (code, output) = OpticalTools.Run("xorriso", "-as", "mkisofs", "-quiet", "-V", "TEST", "-o", iso, tree);
        Assert.True(code == 0, output);
        return File.ReadAllBytes(iso);
    }

    [NeedsToolFact("xorriso")]
    public void AacsFolderMarksABluRayAsProtected()
    {
        var finding = CopyProtectionDetector.Inspect(new FakeSectorReader(BuildIso("AACS", "BDMV", "CERTIFICATE")));

        Assert.NotNull(finding);
        Assert.Equal(ProtectionSystem.Aacs, finding.System);
    }

    [NeedsToolFact("xorriso")]
    public void BdSvmFolderMeansBdPlus()
    {
        var finding = CopyProtectionDetector.Inspect(new FakeSectorReader(BuildIso("AACS", "BDSVM", "BDMV")));

        Assert.Equal(ProtectionSystem.BdPlus, finding!.System);
    }

    [NeedsToolFact("xorriso")]
    public void FolderNamesAreMatchedIgnoringCase()
    {
        var finding = CopyProtectionDetector.Inspect(new FakeSectorReader(BuildIso("aacs")));

        Assert.NotNull(finding);
    }

    [NeedsToolFact("xorriso")]
    public void PlainDataDiscIsNotProtected()
    {
        Assert.Null(CopyProtectionDetector.Inspect(new FakeSectorReader(BuildIso("docs", "photos"))));
    }

    [NeedsToolFact("xorriso")]
    public void HomeMadeDvdVideoWithoutCopyrightFlagIsNotProtected()
    {
        var reader = new FakeSectorReader(BuildIso("VIDEO_TS", "AUDIO_TS")) { Copyright = new DiscCopyrightInfo(ProtectionSystem.None, 0xFF) };

        Assert.Null(CopyProtectionDetector.Inspect(reader));
    }

    [NeedsToolFact("xorriso")]
    public void CopyrightStructureWinsOverTheFileSystem()
    {
        var reader = new FakeSectorReader(BuildIso("VIDEO_TS")) { Copyright = new DiscCopyrightInfo(ProtectionSystem.Css, 0) };

        Assert.Equal(ProtectionSystem.Css, CopyProtectionDetector.Inspect(reader)!.System);
    }

    [NeedsToolFact("xorriso")]
    public async Task RippingAnAacsDiscIsRefusedBeforeAnySectorIsCopied()
    {
        var reader = new FakeSectorReader(BuildIso("AACS"));
        var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<BootrixException>(
            () => new DiscRipper().RipAsync(reader, destination, new RipOptions { RetryDelay = TimeSpan.Zero }));

        Assert.Equal(ErrorCode.CopyProtected, ex.Code);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public void RandomDataIsNotProtected()
    {
        // a file system reader will choke on this; that must not be taken for protection or crash the check
        Assert.Null(CopyProtectionDetector.Inspect(new FakeSectorReader(OpticalTestData.DiscImage(400))));
    }

    [Fact]
    public void UnreadableDiscIsNotProtected()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(400));
        for (var lba = 0; lba < 400; lba++)
        {
            reader.PermanentlyBad.Add(lba);
        }

        Assert.Null(CopyProtectionDetector.Inspect(reader));
    }

    [Fact]
    public void LostDriveIsNotSwallowed()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(400));
        reader.BeforeRead = (_, _) => throw new BootrixException(ErrorCode.DeviceRemoved, "gone");

        Assert.Throws<BootrixException>(() => CopyProtectionDetector.Inspect(reader));
    }

    [Theory]
    [InlineData(0, ProtectionSystem.None)]
    [InlineData(1, ProtectionSystem.Css)]
    [InlineData(2, ProtectionSystem.Cprm)]
    [InlineData(3, ProtectionSystem.Other)]
    [InlineData(0x10, ProtectionSystem.Other)]
    public void CopyrightDescriptorTypesAreMapped(byte type, ProtectionSystem expected)
    {
        var info = DiscCopyrightInfo.FromDescriptor(type, 0x3F);

        Assert.Equal(expected, info.System);
        Assert.Equal(0x3F, info.RegionInformation);
    }

    [NeedsToolFact("xorriso", "isoinfo")]
    public async Task RippedRealIsoIsReadableByIndependentTools()
    {
        var iso = BuildIso("docs", "photos");
        var reader = new FakeSectorReader(iso);
        var output = Path.Combine(_dir, "ripped.iso");

        var report = await new DiscRipper().RipToFileAsync(reader, output, new RipOptions { RetryDelay = TimeSpan.Zero });

        Assert.True(report.IsComplete);
        Assert.Equal(iso, File.ReadAllBytes(output));
        var (code, listing) = OpticalTools.Run("isoinfo", "-l", "-i", output);
        Assert.True(code == 0, listing);
        Assert.Contains("README.TXT", listing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PHOTOS", listing, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output + ".btxrip"));
    }
}
