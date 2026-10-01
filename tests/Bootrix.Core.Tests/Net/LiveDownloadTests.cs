// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;

namespace Bootrix.Core.Tests.Net;

public class LiveDownloadTests
{
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
}
