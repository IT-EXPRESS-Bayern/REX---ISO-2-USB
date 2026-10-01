// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class ChecksumSourceTests
{
    private const string Sums = "https://vendor.example/SHA256SUMS";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private DistroHttp Http(FakeWeb web) => new(web.CreateClient(), _time);

    private static FakeWeb DebianFiles() => new FakeWeb()
        .ServeFixture(Sums, "debian/netinst.SHA256SUMS")
        .ServeFixture(Sums + ".sign", "debian/netinst.SHA256SUMS.sign");

    [Fact]
    public async Task Detached_ReturnsTheParsedFileWhenTheSignatureIsFromAPinnedKey()
    {
        var file = await ChecksumSource.DetachedAsync(Http(DebianFiles()), new Uri(Sums), new Uri(Sums + ".sign"), DistroKeys.Debian, CancellationToken.None);

        Assert.Equal(3, file.Entries.Count);
        Assert.Equal("a7ef94ac2fb9a7fec454552abd629b7cc9d5155c886165a45649f5ce6167e355", file.Pick("debian-13.7.0-amd64-netinst.iso").Hex);
    }

    [Fact]
    public async Task Detached_RejectsAKeyThatIsInTheKeyringButNotPinned()
    {
        // The Debian key is a valid member of this keyring, but only Ubuntu's fingerprint is pinned.
        var keys = DistroKeys.Debian.Restrict("843938DF228D22F7B3742BC0D94AA3F0EFE21092");

        var error = await Assert.ThrowsAsync<BootrixException>(() =>
            ChecksumSource.DetachedAsync(Http(DebianFiles()), new Uri(Sums), new Uri(Sums + ".sign"), keys, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
        Assert.Contains("KeyNotAllowed", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detached_RejectsAKeyThatIsNotInTheKeyringAtAll()
    {
        var error = await Assert.ThrowsAsync<BootrixException>(() =>
            ChecksumSource.DetachedAsync(Http(DebianFiles()), new Uri(Sums), new Uri(Sums + ".sign"), DistroKeys.Ubuntu, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
        Assert.Contains("KeyUnknown", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detached_RejectsAGarbageSignature()
    {
        var web = DebianFiles().Serve(Sums + ".sign", "not a signature");

        var error = await Assert.ThrowsAsync<BootrixException>(() =>
            ChecksumSource.DetachedAsync(Http(web), new Uri(Sums), new Uri(Sums + ".sign"), DistroKeys.Debian, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Signed_DocumentsAreNeverTakenFromTheCache()
    {
        var web = DebianFiles();
        var http = Http(web);

        await http.GetBytesAsync(new Uri(Sums), CancellationToken.None);
        await ChecksumSource.DetachedAsync(http, new Uri(Sums), new Uri(Sums + ".sign"), DistroKeys.Debian, CancellationToken.None);
        await ChecksumSource.DetachedAsync(http, new Uri(Sums), new Uri(Sums + ".sign"), DistroKeys.Debian, CancellationToken.None);

        Assert.Equal(3, web.Count(Sums));
        Assert.Equal(2, web.Count(Sums + ".sign"));
    }

    [Fact]
    public async Task ClearSigned_ReturnsOnlyTheSignedText()
    {
        var web = new FakeWeb().ServeFixture("https://vendor.example/CHECKSUM", "fedora/Fedora-Workstation-44-1.7-x86_64-CHECKSUM");

        var file = await ChecksumSource.ClearSignedAsync(Http(web), new Uri("https://vendor.example/CHECKSUM"), DistroKeys.FedoraFor(44), CancellationToken.None);

        var entry = Assert.Single(file.Entries);
        Assert.Equal("Fedora-Workstation-Live-44-1.7.x86_64.iso", entry.FileName);
    }

    [Fact]
    public async Task ClearSigned_RejectsTextAddedAroundTheSignature()
    {
        var signed = DistroFixtures.Text("fedora/Fedora-Workstation-44-1.7-x86_64-CHECKSUM");
        var web = new FakeWeb().Serve("https://vendor.example/CHECKSUM", signed + "SHA256 (evil.iso) = " + new string('a', 64) + "\n");

        var error = await Assert.ThrowsAsync<BootrixException>(() =>
            ChecksumSource.ClearSignedAsync(Http(web), new Uri("https://vendor.example/CHECKSUM"), DistroKeys.FedoraFor(44), CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public void Pick_PrefersSha256_ThenTheStrongestAvailable_AndNeverMd5()
    {
        var all = ChecksumFile.Parse(string.Join(
            '\n',
            $"{new string('1', 32)}  a.iso",
            $"{new string('2', 40)}  a.iso",
            $"{new string('3', 64)}  a.iso",
            $"{new string('4', 128)}  a.iso"));
        var strong = ChecksumFile.Parse($"{new string('4', 128)}  b.iso\n{new string('1', 32)}  b.iso");
        var weak = ChecksumFile.Parse($"{new string('1', 32)}  c.iso");

        Assert.Equal(HashKind.Sha256, all.Pick("a.iso").Kind);
        Assert.Equal(HashKind.Sha512, strong.Pick("b.iso").Kind);
        Assert.Equal(ErrorCode.ChecksumFileInvalid, Assert.Throws<BootrixException>(() => weak.Pick("c.iso")).Code);
    }
}
