// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;

namespace Bootrix.Core.Tests.Net;

public class LiveDownloadTests
{
    private static readonly HttpClient Http = new();

    [LiveFact]
    public async Task AlpineMiniRootfsDownloadsOverRealHttpsWithSegmentsAndMatchesThePublishedDigest()
    {
        using var dir = new TempDirectory();
        var url = new Uri("https://dl-cdn.alpinelinux.org/alpine/v3.20/releases/x86_64/alpine-minirootfs-3.20.3-x86_64.tar.gz");
        var request = new DownloadRequest(url)
        {
            Options = new DownloadOptions { MaxSegments = 4, MinSegmentSize = 256 * 1024 },
            ExpectedHashes = [new FileHash(HashKind.Sha256, "d4e6fd67dcf75e40c451560ac7265166c2b72a0f38ddc9aae756a7de3d1efa0c")],
        };

        var result = await new SegmentedDownloader().DownloadAsync(request, dir.File("rootfs.tar.gz"));

        Assert.Equal(3_490_290, result.Length);
        Assert.Equal("d4e6fd67dcf75e40c451560ac7265166c2b72a0f38ddc9aae756a7de3d1efa0c", result.Sha256);
    }

    [LiveFact]
    public async Task CurrentUbuntuChecksumSignatureValidatesWithThePinnedKey()
    {
        byte[] sums, signature;
        try
        {
            sums = await Http.GetByteArrayAsync("https://releases.ubuntu.com/24.04/SHA256SUMS");
            signature = await Http.GetByteArrayAsync("https://releases.ubuntu.com/24.04/SHA256SUMS.gpg");
        }
        catch (HttpRequestException)
        {
            // No network: nothing to check, and a missing connection is no failure of Bootrix.
            return;
        }

        var result = OpenPgpVerifier.Verify(sums, signature, OpenPgpKeyring.Load(NetFixtures.Bytes("ubuntu/ubuntu-cd-signing-key.asc")));

        Assert.Equal(OpenPgpStatus.Valid, result.Status);
        Assert.Equal("843938DF228D22F7B3742BC0D94AA3F0EFE21092", result.Fingerprint);
        Assert.NotEmpty(ChecksumFile.Parse(sums).Entries);
    }

    [LiveFact]
    public async Task DebianPublishesTheFingerprintOfTheCdSigningKeyThatIsPinnedInTests()
    {
        string page;
        try
        {
            page = await Http.GetStringAsync("https://www.debian.org/CD/verify");
        }
        catch (HttpRequestException)
        {
            return;
        }

        Assert.Contains("DF9B 9C49 EAA9 2984 3258  9D76 DA87 E80D 6294 BE9B", page, StringComparison.Ordinal);
    }
}
