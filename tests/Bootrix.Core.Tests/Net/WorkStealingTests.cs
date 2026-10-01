// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using static Bootrix.Core.Tests.Net.Support.DownloadTestSupport;

namespace Bootrix.Core.Tests.Net;

public class WorkStealingTests
{
    private static bool IsFirstSegment(RequestInfo info) => info is { RangeStart: 0, RangeEnd: > 0 };

    [Fact]
    public async Task IdleConnectionsTakeOverTheTailOfASlowSegment()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);

        // Without stealing the first quarter would need 2 MiB at 300 kB/s, about seven seconds.
        server.Script = info => IsFirstSegment(info) ? Fault.Throttle(300_000) : Fault.None;

        await DownloadTestSupport.Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(maxSegments: 4) },
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));

        // What the slow connection sent is the proof, not the wall clock: it was cut short because the idle
        // connections took its second half (and then more of the rest), so it never delivered its whole range.
        var firstQuarter = content.Length / 4;
        var steals = server.Requests.Where(r => r.RangeStart > 0 && r.RangeStart < firstQuarter).ToList();
        Assert.NotEmpty(steals);

        // The server notices the dropped connection a moment after the download has finished.
        RequestRecord? slow = null;
        Assert.True(
            await EventuallyAsync(() => (slow = server.Requests.Where(r => r is { RangeStart: 0, RangeEnd: > 0 }).OrderBy(r => r.Index).FirstOrDefault()) is not null),
            "the slow request was never completed on the server");
        Assert.True(slow!.BytesSent <= firstQuarter / 2 + 64 * 1024, $"the slow connection delivered {slow.BytesSent} bytes");
    }

    [Fact]
    public async Task StolenSegmentsAreSplitRepeatedlyDownToTheMinimumSize()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => IsFirstSegment(info) ? Fault.Throttle(200_000) : Fault.None;

        await DownloadTestSupport.Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(maxSegments: 4, minSegmentSize: 128 * 1024) },
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        var insideFirstQuarter = server.Requests.Where(r => r.RangeStart > 0 && r.RangeStart < content.Length / 4).Select(r => r.RangeStart).Distinct().Count();
        Assert.True(insideFirstQuarter >= 2, $"expected repeated splits, saw {insideFirstQuarter}");
    }

    [Fact]
    public async Task SplitsHappenOnPieceBoundaries()
    {
        var content = RandomBytes(8 * MiB);
        var pieces = PiecesOf(content, 512 * 1024);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => IsFirstSegment(info) ? Fault.Throttle(300_000) : Fault.None;

        await DownloadTestSupport.Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(maxSegments: 4), Pieces = pieces },
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        var segmentStarts = server.Requests.Where(r => r.RangeStart > 0).Select(r => r.RangeStart!.Value).Distinct().ToList();
        Assert.NotEmpty(segmentStarts);
        Assert.All(segmentStarts, start => Assert.Equal(0, start % (512 * 1024)));
    }

    [Fact]
    public async Task SingleSegmentSmallFileDoesNotSplit()
    {
        var content = RandomBytes(300 * 1024);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = _ => Fault.Throttle(1_000_000);

        await DownloadTestSupport.Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(maxSegments: 4, minSegmentSize: 256 * 1024) },
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(2, server.RequestCount);
    }
}
