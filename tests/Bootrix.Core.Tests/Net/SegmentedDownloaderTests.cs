// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using static Bootrix.Core.Tests.Net.Support.DownloadTestSupport;

namespace Bootrix.Core.Tests.Net;

public class SegmentedDownloaderTests
{
    [Fact]
    public async Task DownloadsIdenticalBytesOverSeveralSegments()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        var progress = new ProgressLog();

        var result = await Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(maxSegments: 4) },
            dir.File("image.iso"),
            progress);

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("image.iso")));
        Assert.Equal(content.Length, result.Length);
        Assert.Equal(Sha256Hex(content), result.Sha256);
        Assert.Equal(0, result.ResumedBytes);
        Assert.Equal(server.FileUri, result.FinalUrl);
        Assert.Equal("\"v1\"", result.ETag);

        var segmentStarts = server.Requests.Where(r => r.RangeStart > 0).Select(r => r.RangeStart).Distinct().Count();
        Assert.True(segmentStarts >= 3, $"expected several segments, saw {segmentStarts}");
        Assert.Equal([dir.File("image.iso")], Directory.GetFiles(dir.Path));
        Assert.Contains(progress.Reports, r => r.Phase == DownloadPhase.Connecting);
        Assert.Contains(progress.Reports, r => r.Phase == DownloadPhase.Verifying && r.BytesDone == content.Length);
        Assert.Equal(content.Length, progress.Reports.Last(r => r.Phase == DownloadPhase.Downloading).BytesDone);
    }

    [Fact]
    public async Task RangeSupportIsProbedWithAOneByteRequest()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(2 * MiB));

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        var probe = server.Requests.OrderBy(r => r.Index).First();
        Assert.Equal((0, 0), (probe.RangeStart, probe.RangeEnd));
        Assert.Equal(206, probe.Status);
        Assert.Equal(1, probe.BytesSent);
    }

    [Fact]
    public async Task AcceptRangesHeaderAloneIsNotTrusted()
    {
        var content = RandomBytes(3 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.SupportsRanges = false;
        server.LieAboutRanges = true;

        var result = await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(Sha256Hex(content), result.Sha256);
        Assert.Equal(1, server.PeakConcurrency);
    }

    [Fact]
    public async Task ServerWithoutRangesIsServedWithASingleRequest()
    {
        var content = RandomBytes(2 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.SupportsRanges = false;

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task MissingContentLengthStillDownloads()
    {
        var content = RandomBytes(2 * MiB + 123);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.SupportsRanges = false;
        server.OmitContentLength = true;
        var progress = new ProgressLog();

        var result = await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"), progress);

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(content.Length, result.Length);
        Assert.All(progress.Reports.Where(r => r.Phase == DownloadPhase.Downloading), r => Assert.Null(r.BytesTotal));
    }

    [Fact]
    public async Task EmptyFileIsDownloaded()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync([]);

        var result = await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("empty.bin"));

        Assert.Equal(0, new FileInfo(dir.File("empty.bin")).Length);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", result.Sha256);
    }

    [Fact]
    public async Task FileSmallerThanOneSegmentUsesOneConnection()
    {
        var content = RandomBytes(100_000);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions(minSegmentSize: MiB) }, dir.File("small.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("small.bin")));
        Assert.Equal(1, server.PeakConcurrency);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public async Task ParallelConnectionsNeverExceedTheLimit(int maxSegments)
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = _ => Fault.Throttle(4_000_000);

        await Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(maxSegments, 128 * 1024) },
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.InRange(server.PeakConcurrency, 2, maxSegments);
    }

    [Fact]
    public async Task RedirectChainIsResolvedOnceAndSegmentsGoToTheFinalAddress()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Redirects["/start"] = "/hop";
        server.Redirects["/hop"] = new Uri(server.BaseAddress, "/file").AbsoluteUri;

        var result = await Downloader().DownloadAsync(
            new DownloadRequest(new Uri(server.BaseAddress, "/start")) { Options = FastOptions() },
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(1, server.Hits("/start"));
        Assert.Equal(1, server.Hits("/hop"));
        Assert.True(server.Hits("/file") >= 3);
        Assert.Equal(server.FileUri, result.FinalUrl);
    }

    [Fact]
    public async Task RedirectLoopEndsWithADownloadError()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(1000));
        server.Redirects["/a"] = "/b";
        server.Redirects["/b"] = "/a";

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(new Uri(server.BaseAddress, "/a")) { Options = FastOptions() with { MaxRedirects = 5 } },
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Equal(6, server.Hits("/a") + server.Hits("/b"));
    }

    [Fact]
    public async Task NotFoundIsPermanentAndNotRetried()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(1000));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(new Uri(server.BaseAddress, "/missing")) { Options = FastOptions() },
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Equal(["HTTP 404"], ex.Arguments);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task AllExpectedHashesAreCheckedAndReturned()
    {
        var content = RandomBytes(3 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);

        var result = await Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(), ExpectedHashes = HashesOf(content), ExpectedSize = content.Length },
            dir.File("a.bin"));

        Assert.Equal(3, result.Hashes.Count);
        Assert.Equivalent(HashesOf(content), result.Hashes);
    }

    [Theory]
    [InlineData(HashKind.Sha256)]
    [InlineData(HashKind.Sha1)]
    [InlineData(HashKind.Sha512)]
    public async Task HashMismatchDeletesTheFileAndReportsIt(HashKind wrong)
    {
        var content = RandomBytes(3 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        var expected = HashesOf(content).Select(h => h.Kind == wrong ? new FileHash(h.Kind, new string('0', h.Hex.Length)) : h).ToList();

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(), ExpectedHashes = expected },
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadHashMismatch, ex.Code);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task MismatchLeavesAnExistingDestinationUntouched()
    {
        var content = RandomBytes(MiB);
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(dir.File("a.bin"), "previous");
        await using var server = await FileServer.StartAsync(content);

        await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(), ExpectedHashes = [new FileHash(HashKind.Sha256, new string('1', 64))] },
            dir.File("a.bin")));

        Assert.Equal("previous", await File.ReadAllTextAsync(dir.File("a.bin")));
    }

    [Fact]
    public async Task VerifiedDownloadReplacesAnExistingDestination()
    {
        var content = RandomBytes(MiB);
        using var dir = new TempDirectory();
        await File.WriteAllTextAsync(dir.File("a.bin"), "previous");
        await using var server = await FileServer.StartAsync(content);

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions(), ExpectedHashes = HashesOf(content) }, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
    }

    [Fact]
    public async Task ServerSizeThatContradictsTheCatalogIsRejectedBeforeDownloading()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(2 * MiB));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(), ExpectedSize = 3 * MiB },
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task ProgressReportsSpeedEtaAndActiveSegments()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = _ => Fault.Throttle(3_000_000);
        var progress = new ProgressLog();

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions(maxSegments: 3) }, dir.File("a.bin"), progress);

        var downloading = progress.Reports.Where(r => r.Phase == DownloadPhase.Downloading).ToList();
        Assert.True(downloading.Count > 5);
        Assert.Equal(downloading.Select(r => r.BytesDone).Order(), downloading.Select(r => r.BytesDone));
        Assert.All(downloading, r => Assert.Equal(content.Length, r.BytesTotal));
        Assert.Contains(downloading, r => r.BytesPerSecond > 0);
        Assert.Contains(downloading, r => r.Eta is not null);
        Assert.Contains(downloading, r => r.ActiveSegments >= 2);
        Assert.InRange(downloading.Max(r => r.ActiveSegments), 1, 3);
    }

    [Fact]
    public async Task DefaultHttpHandlerWorksAgainstLoopback()
    {
        var content = RandomBytes(2 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);

        var result = await new SegmentedDownloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(Sha256Hex(content), result.Sha256);
    }

    [Fact]
    public async Task UnsupportedAlgorithmInExpectedHashesIsRejectedUpFront()
    {
        using var dir = new TempDirectory();
        var request = new DownloadRequest(new Uri("http://localhost/x")) { ExpectedHashes = [new FileHash(HashKind.Md5, new string('a', 32))] };

        await Assert.ThrowsAsync<ArgumentException>(() => Downloader().DownloadAsync(request, dir.File("a.bin")));
    }

    [Fact]
    public void OnlyWebAddressesAreAccepted()
    {
        Assert.Throws<ArgumentException>(() => new DownloadRequest(new Uri("file:///etc/passwd")));
        Assert.Throws<ArgumentException>(() => new DownloadRequest(new Uri("ftp://example.org/a")));
    }
}
