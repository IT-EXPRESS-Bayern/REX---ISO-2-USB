// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Net;

public class ChecksumFileTests
{
    private const string Sha256A = "3a4c9877b483ab46d7c3fbe165a0db275e1ae3cfe56a5657e5a47c2f99a99d1e";
    private const string Sha256B = "e907d92eeec9df64163a7e454cbc8d7755e8ddc7ed42f99dbc80c40f1a138433";

    [Theory]
    [InlineData("ubuntu/SHA256SUMS", "ubuntu-24.04.4-desktop-amd64.iso", "3a4c9877b483ab46d7c3fbe165a0db275e1ae3cfe56a5657e5a47c2f99a99d1e")]
    [InlineData("ubuntu/SHA256SUMS", "ubuntu-24.04.5-wsl-amd64.wsl", "bb415d824822c4b878125729af451a5d18fb13d1cf5cbed9a7393ad64ac6039e")]
    [InlineData("debian/SHA256SUMS", "debian-13.7.0-amd64-netinst.iso", "a7ef94ac2fb9a7fec454552abd629b7cc9d5155c886165a45649f5ce6167e355")]
    [InlineData("fedora/CHECKSUM", "Fedora-Workstation-Live-44-1.7.x86_64.iso", "1620295f6a00c27c3208f0c00b8ece4eab1ec69b9002152d97488bf26a426ddf")]
    [InlineData("checksums/arch-sha256sums.txt", "archlinux-x86_64.iso", "be8458032f8105e60ee2a3067f950b6e3c007ee51b38dac50e8b48e765561c91")]
    [InlineData("checksums/mint-sha256sum.txt", "linuxmint-22.2-xfce-64bit.iso", "dea13e523dca28e3aa48d90167a6368c63e1b3251492115417fdbf648551558f")]
    public void RealDistributionFilesYieldTheListedDigest(string fixture, string fileName, string expected)
    {
        var checksums = ChecksumFile.Parse(NetFixtures.Bytes(fixture));

        var hash = checksums.Select(fileName);

        Assert.Equal(HashKind.Sha256, hash.Kind);
        Assert.Equal(expected, hash.Hex);
    }

    [Fact]
    public void ClearSignedBsdFileIgnoresArmorAndComments()
    {
        var checksums = ChecksumFile.Parse(NetFixtures.Text("fedora/CHECKSUM"));

        var entry = Assert.Single(checksums.Entries);
        Assert.Equal("Fedora-Workstation-Live-44-1.7.x86_64.iso", entry.FileName);
    }

    [Fact]
    public void BinaryFlagAndTextModeAreNotPartOfTheName()
    {
        var checksums = ChecksumFile.Parse($"{Sha256A} *with-star.iso\n{Sha256B}  two-spaces.iso\n{Sha256A}\ttab.iso\n");

        Assert.Equal(["with-star.iso", "two-spaces.iso", "tab.iso"], checksums.Entries.Select(e => e.FileName));
    }

    [Fact]
    public void SectionedFileKeepsAllAlgorithmsAndSkipsBlake()
    {
        var checksums = ChecksumFile.Parse(NetFixtures.Text("checksums/clonezilla-CHECKSUMS.TXT"));

        var iso = checksums.Find("clonezilla-live-3.3.3-37-amd64.iso");

        Assert.Equal(
            [HashKind.Md5, HashKind.Sha1, HashKind.Sha256, HashKind.Sha512],
            iso.Select(e => e.Hash.Kind).Order());
        Assert.Equal(8, checksums.Entries.Count);
    }

    [Fact]
    public void SectionedFileSelectsStrongestAcceptedAlgorithm()
    {
        var checksums = ChecksumFile.Parse(NetFixtures.Text("checksums/gparted-CHECKSUMS.TXT"));

        var best = checksums.Select("gparted-live-1.8.1-3-amd64.iso");
        var sha256 = checksums.Select("gparted-live-1.8.1-3-amd64.iso", HashKind.Sha256);

        Assert.Equal(HashKind.Sha512, best.Kind);
        Assert.StartsWith("926e34cf", best.Hex, StringComparison.Ordinal);
        Assert.Equal("3f66b2e10b8bb2c573ed6cdd3a9b54fd0a8e7690634ab6b15c3c8f517992d1a1", sha256.Hex);
    }

    [Fact]
    public void Blake2SectionIsNotMistakenForSha512()
    {
        var checksums = ChecksumFile.Parse(NetFixtures.Text("checksums/clonezilla-CHECKSUMS.TXT"));

        var sha512 = checksums.Select("clonezilla-live-3.3.3-37-amd64.zip", HashKind.Sha512);

        Assert.StartsWith("d5514020", sha512.Hex, StringComparison.Ordinal);
    }

    [Fact]
    public void Md5IsOnlyReturnedWhenAskedForExplicitly()
    {
        var checksums = ChecksumFile.Parse($"{new string('a', 32)}  legacy.iso\n");

        var ex = Assert.Throws<BootrixException>(() => checksums.Select("legacy.iso"));
        Assert.Equal(ErrorCode.ChecksumFileInvalid, ex.Code);
        Assert.Equal(HashKind.Md5, checksums.Select("legacy.iso", HashKind.Md5).Kind);
    }

    [Fact]
    public void HashOnlyFileMatchesAnyName()
    {
        var checksums = ChecksumFile.Parse(NetFixtures.Text("checksums/truenas-26.0.0-BETA.3.iso.sha256"));

        var hash = checksums.Select("TrueNAS-26.0.0-BETA.3.iso");

        Assert.Equal("5a4e174e4583b86a005015cacafc681eae91fc042df38354b42b376204416ada", hash.Hex);
        Assert.Null(Assert.Single(checksums.Entries).FileName);
    }

    [Fact]
    public void PathPrefixInFileMatchesBareFileName()
    {
        var checksums = ChecksumFile.Parse(
            $"{Sha256A}  v8.10/memtest86plus-8.10.iso\n{Sha256B}  v8.10/memtest86plus-8.10.usb\n");

        Assert.Equal(Sha256A, checksums.Select("memtest86plus-8.10.iso").Hex);
        Assert.Equal(Sha256B, checksums.Select("v8.10/memtest86plus-8.10.usb").Hex);
        Assert.Equal(Sha256A, checksums.Select("downloads/v8.10/memtest86plus-8.10.iso").Hex);
    }

    [Fact]
    public void BareNameInFileMatchesPathOnOurSide()
    {
        var checksums = ChecksumFile.Parse($"{Sha256A}  ./image.iso\n");

        Assert.Equal(Sha256A, checksums.Select(@"C:\Users\me\Downloads\image.iso").Hex);
    }

    [Fact]
    public void SameBaseNameInTwoDirectoriesIsAmbiguousUntilQualified()
    {
        var checksums = ChecksumFile.Parse($"{Sha256A}  x64/memtest.efi\n{Sha256B}  x32/memtest.efi\n");

        Assert.Throws<BootrixException>(() => checksums.Select("memtest.efi"));
        Assert.Equal(Sha256B, checksums.Select("x32/memtest.efi").Hex);
    }

    [Fact]
    public void ExactCaseWinsOverCaseInsensitiveMatch()
    {
        var checksums = ChecksumFile.Parse($"{Sha256A}  Image.ISO\n{Sha256B}  image.iso\n");

        Assert.Equal(Sha256B, checksums.Select("image.iso").Hex);
        Assert.Equal(Sha256A, checksums.Select("Image.ISO").Hex);
        Assert.Throws<BootrixException>(() => checksums.Select("IMAGE.ISO"));
    }

    [Fact]
    public void CaseDifferenceIsToleratedWhenUnique()
    {
        var checksums = ChecksumFile.Parse($"{Sha256A.ToUpperInvariant()}  Setup.ISO\n");

        Assert.Equal(Sha256A, checksums.Select("setup.iso").Hex);
    }

    [Fact]
    public void ContradictingDuplicatesAreRejected()
    {
        var checksums = ChecksumFile.Parse($"{Sha256A}  a.iso\n{Sha256B}  a.iso\n");

        var ex = Assert.Throws<BootrixException>(() => checksums.Select("a.iso"));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, ex.Code);
        Assert.Equal(["a.iso"], ex.Arguments);
    }

    [Fact]
    public void IdenticalDuplicatesAreHarmless()
    {
        var checksums = ChecksumFile.Parse(NetFixtures.Text("checksums/arch-sha256sums.txt"));

        Assert.Equal(4, checksums.Entries.Count);
        Assert.Equal(
            checksums.Select("archlinux-2026.09.01-x86_64.iso"),
            checksums.Select("archlinux-x86_64.iso"));
    }

    [Fact]
    public void BsdLineNamesTheAlgorithm()
    {
        var sha512 = new string('c', 128);
        var checksums = ChecksumFile.Parse($"SHA512 (disc.iso) = {sha512}\nSHA256 (disc.iso) = {Sha256A}\nBLAKE2b (disc.iso) = {sha512}\n");

        Assert.Equal(2, checksums.Entries.Count);
        Assert.Equal(sha512, checksums.Select("disc.iso").Hex);
    }

    [Fact]
    public void BsdTagThatContradictsTheDigestLengthIsDropped()
    {
        var checksums = ChecksumFile.Parse($"SHA256 (disc.iso) = {new string('c', 128)}\n");

        Assert.Empty(checksums.Entries);
    }

    [Fact]
    public void WindowsLineEndingsAndByteOrderMarkAreAccepted()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(System.Text.Encoding.UTF8.GetBytes($"{Sha256A} *a.iso\r\n{Sha256B} *b.iso\r\n"))
            .ToArray();

        var checksums = ChecksumFile.Parse(bytes);

        Assert.Equal(["a.iso", "b.iso"], checksums.Entries.Select(e => e.FileName));
    }

    [Fact]
    public void GnuEscapedNamesAreDecoded()
    {
        var checksums = ChecksumFile.Parse($"\\{Sha256A}  odd\\\\name.iso\n");

        Assert.Equal("odd/name.iso", Assert.Single(checksums.Entries).FileName);
    }

    [Fact]
    public void DashEscapedLinesInsideClearSignedTextAreUnescaped()
    {
        var text = $"-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n- {Sha256A}  a.iso\n-----BEGIN PGP SIGNATURE-----\n\n{Sha256B}\n-----END PGP SIGNATURE-----\n";

        var checksums = ChecksumFile.Parse(text);

        var entry = Assert.Single(checksums.Entries);
        Assert.Equal("a.iso", entry.FileName);
    }

    [Theory]
    [InlineData("<html><body>404 Not Found</body></html>")]
    [InlineData("")]
    [InlineData("deadbeef  too-short.iso")]
    [InlineData("zzzz9877b483ab46d7c3fbe165a0db275e1ae3cfe56a5657e5a47c2f99a99d1e  not-hex.iso")]
    public void UnusableContentGivesCleanErrorFromSelect(string content)
    {
        var checksums = ChecksumFile.Parse(content);

        var ex = Assert.Throws<BootrixException>(() => checksums.Select("a.iso"));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, ex.Code);
    }

    [Fact]
    public void NonSectionHeadingsAreJustComments()
    {
        var checksums = ChecksumFile.Parse($"### Release notes\n{Sha256A}  a.iso\n");

        Assert.Equal(Sha256A, checksums.Select("a.iso").Hex);
    }

    [Fact]
    public void TryGetHashFindsRequestedAlgorithmOnly()
    {
        var checksums = ChecksumFile.Parse(NetFixtures.Text("checksums/clonezilla-CHECKSUMS.TXT"));

        Assert.True(checksums.TryGetHash("clonezilla-live-3.3.3-37-amd64.iso", HashKind.Sha1, out var sha1));
        Assert.Equal("2d28c4fd49d28bb18d7cea8e25e2f09b9c939d8b", sha1!.Hex);
        Assert.False(checksums.TryGetHash("clonezilla-live-3.3.3-37-amd64.iso", HashKind.Sha384, out _));
        Assert.False(checksums.TryGetHash("missing.iso", HashKind.Sha1, out _));
    }
}
