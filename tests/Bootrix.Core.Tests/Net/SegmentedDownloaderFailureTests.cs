// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using static Bootrix.Core.Tests.Net.Support.DownloadTestSupport;

namespace Bootrix.Core.Tests.Net;

public class SegmentedDownloaderFailureTests
{
    [Fact]
    public async Task ConnectionDroppedMidSegmentIsResumedWhereItStopped()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => info is { RangeStart: > 0, Index: <= 8 } ? Fault.AbortAfter(100 * 1024) : Fault.None;

        var result = await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(Sha256Hex(content), result.Sha256);
        Assert.True(server.Requests.Count(r => r.BytesSent == 100 * 1024) >= 3, "the injected drops should have happened");
    }

    [Fact]
    public async Task ConnectionDroppedWhileProbingIsRetried()
    {
        var content = RandomBytes(2 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => info.Index <= 2 ? Fault.Status(500) : Fault.None;

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal([500, 500, 206], server.Requests.OrderBy(r => r.Index).Take(3).Select(r => r.Status));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task RetryAfterIsHonoured(int status)
    {
        var content = RandomBytes(2 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => info.Index == 2 ? Fault.Status(status, retryAfterSeconds: 1) : Fault.None;
        var clock = Stopwatch.StartNew();

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(900), $"finished after {clock.Elapsed}, the server asked to wait 1 s");
    }

    [Fact]
    public async Task RetryAfterBeyondTheLimitIsShortened()
    {
        var content = RandomBytes(MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => info.Index == 1 ? Fault.Status(503, retryAfterSeconds: 3600) : Fault.None;
        var options = FastOptions() with { MaxRetryAfter = TimeSpan.FromMilliseconds(200) };
        var clock = Stopwatch.StartNew();

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = options }, dir.File("a.bin"));

        Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GivesUpAfterTheRetryBudgetIsSpent()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(MiB));
        server.Script = _ => Fault.Status(503);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions() with { MaxRetries = 3 } },
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Equal(["HTTP 503"], ex.Arguments);
        Assert.Equal(4, server.RequestCount);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task SegmentThatKeepsFailingFailsTheDownloadAndKeepsTheProgress()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => info.RangeStart >= 2 * MiB ? Fault.Status(500) : Fault.None;

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions(maxSegments: 2) with { MaxRetries = 2 } },
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.False(File.Exists(dir.File("a.bin")));
        Assert.True(File.Exists(dir.File("a.bin.part")));
        Assert.True(File.Exists(dir.File("a.bin.btxdl")));
    }

    [Fact]
    public async Task ExpiredLinkIsRenewedThroughTheResolverExactlyOnce()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.ValidTokens.Add("t1");
        var expired = 0;
        server.Script = info =>
        {
            // The token dies while the first four segments are running, and each of them gets cut off once,
            // so every reconnect meets a 403 at the same time.
            if (info.Index == 3 && Interlocked.Exchange(ref expired, 1) == 0)
            {
                server.ValidTokens.Clear();
                server.ValidTokens.Add("t2");
            }

            return info.Index <= 5 ? Fault.AbortAfter(512 * 1024) : Fault.None;
        };

        var resolverCalls = 0;
        var request = new DownloadRequest(new Uri(server.BaseAddress, "/signed?token=t1"))
        {
            Options = FastOptions(),
            LinkResolver = _ =>
            {
                Interlocked.Increment(ref resolverCalls);
                return Task.FromResult(new Uri(server.BaseAddress, "/signed?token=t2"));
            },
        };

        var result = await Downloader().DownloadAsync(request, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(1, resolverCalls);
        Assert.Contains("token=t2", result.FinalUrl.Query, StringComparison.Ordinal);
        Assert.Contains(server.Requests, r => r.Status == 206 && r.RangeStart > 0);
    }

    [Fact]
    public async Task TransientFailureWhileRenewingTheLinkIsRetried()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.ValidTokens.Add("t1");
        var expired = 0;
        var probeFailed = 0;
        server.Script = info =>
        {
            if (info.Index == 3 && Interlocked.Exchange(ref expired, 1) == 0)
            {
                server.ValidTokens.Clear();
                server.ValidTokens.Add("t2");
            }

            // The renewal probe (the next one-byte range request after the initial one) fails once with a server error.
            if (info is { RangeStart: 0, RangeEnd: 0, Index: > 1 } && Interlocked.Exchange(ref probeFailed, 1) == 0)
            {
                return Fault.Status(503);
            }

            return info.Index <= 5 ? Fault.AbortAfter(512 * 1024) : Fault.None;
        };
        var request = new DownloadRequest(new Uri(server.BaseAddress, "/signed?token=t1"))
        {
            Options = FastOptions(),
            LinkResolver = _ => Task.FromResult(new Uri(server.BaseAddress, "/signed?token=t2")),
        };

        await Downloader().DownloadAsync(request, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(1, probeFailed);
    }

    [Fact]
    public async Task ExpiredLinkAtTheStartIsRenewedBeforeDownloading()
    {
        var content = RandomBytes(3 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.ValidTokens.Add("fresh");
        var request = new DownloadRequest(new Uri(server.BaseAddress, "/signed?token=stale"))
        {
            Options = FastOptions(),
            LinkResolver = _ => Task.FromResult(new Uri(server.BaseAddress, "/signed?token=fresh")),
        };

        await Downloader().DownloadAsync(request, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
    }

    [Fact]
    public async Task GoneIsTreatedLikeForbidden()
    {
        var content = RandomBytes(MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        var calls = 0;
        server.Script = info => info.Index == 1 && Interlocked.Increment(ref calls) == 1 ? Fault.Status(410) : Fault.None;
        var resolved = 0;
        var request = new DownloadRequest(server.FileUri)
        {
            Options = FastOptions(),
            LinkResolver = _ =>
            {
                Interlocked.Increment(ref resolved);
                return Task.FromResult(server.FileUri);
            },
        };

        await Downloader().DownloadAsync(request, dir.File("a.bin"));

        Assert.Equal(1, resolved);
        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
    }

    [Fact]
    public async Task ForbiddenWithoutResolverIsFinal()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(MiB));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(new Uri(server.BaseAddress, "/signed?token=none")) { Options = FastOptions() },
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Equal(["HTTP 403"], ex.Arguments);
        Assert.Equal(1, server.Hits("/signed"));
    }

    [Fact]
    public async Task ResolverThatOnlyReturnsDeadLinksGivesUpAfterTheRefreshBudget()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(MiB));
        var calls = 0;
        var request = new DownloadRequest(new Uri(server.BaseAddress, "/signed?token=a"))
        {
            Options = FastOptions() with { MaxLinkRefreshes = 2 },
            LinkResolver = _ => Task.FromResult(new Uri(server.BaseAddress, $"/signed?token=dead{Interlocked.Increment(ref calls)}")),
        };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(request, dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FailingResolverSurfacesAsDownloadError()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(MiB));
        var request = new DownloadRequest(new Uri(server.BaseAddress, "/signed?token=a"))
        {
            Options = FastOptions(),
            LinkResolver = _ => throw new InvalidOperationException("resolver is broken"),
        };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(request, dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Contains("resolver is broken", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileReplacedOnTheServerMidDownloadRestartsFromScratch()
    {
        var oldContent = RandomBytes(6 * MiB, seed: 1);
        var newContent = RandomBytes(6 * MiB, seed: 2);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(oldContent);
        server.Script = info =>
        {
            // The first four segment requests are cut off, the file is swapped, and the reconnects find a different ETag.
            if (info.Index == 6)
            {
                server.Content = newContent;
                server.ETag = "\"v2\"";
            }

            return info.Index is >= 2 and <= 5 ? Fault.AbortAfter(256 * 1024) : Fault.None;
        };

        var result = await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(newContent, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(Sha256Hex(newContent), result.Sha256);
        Assert.Equal("\"v2\"", result.ETag);
        Assert.Equal(0, result.ResumedBytes);
    }

    [Fact]
    public async Task FileOfDifferentLengthMidDownloadRestartsFromScratch()
    {
        var oldContent = RandomBytes(6 * MiB, seed: 1);
        var newContent = RandomBytes(5 * MiB + 7, seed: 2);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(oldContent);
        server.Script = info =>
        {
            if (info.Index == 6)
            {
                server.Content = newContent;
                server.ETag = "\"v2\"";
            }

            return info.Index is >= 2 and <= 5 ? Fault.AbortAfter(256 * 1024) : Fault.None;
        };

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(newContent, await File.ReadAllBytesAsync(dir.File("a.bin")));
    }

    [Fact]
    public async Task ServerThatStopsHonouringRangesMidDownloadEndsInASingleStreamRestart()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info =>
        {
            if (info.Index == 6)
            {
                server.SupportsRanges = false;
            }

            return info.Index is >= 2 and <= 5 ? Fault.AbortAfter(256 * 1024) : Fault.None;
        };

        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
    }

    [Fact]
    public async Task SingleStreamStartsOverAfterAnInterruption()
    {
        var content = RandomBytes(3 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.SupportsRanges = false;
        server.Script = info => info.Index <= 2 ? Fault.AbortAfter(500 * 1024) : Fault.None;
        var progress = new ProgressLog();

        var result = await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, dir.File("a.bin"), progress);

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(Sha256Hex(content), result.Sha256);
        Assert.Equal(3, server.RequestCount);
    }

    [Fact]
    public async Task SingleStreamLeavesNoPartFileWhenItGivesUp()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(2 * MiB));
        server.SupportsRanges = false;
        server.Script = _ => Fault.AbortAfter(100 * 1024);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(server.FileUri) { Options = FastOptions() with { MaxRetries = 2 } },
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task StalledConnectionIsAbandonedAndRetried()
    {
        var content = RandomBytes(2 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => info is { RangeStart: > 0, Index: <= 3 } ? Fault.Throttle(1_000) : Fault.None;
        var options = FastOptions(maxSegments: 2) with { StallTimeout = TimeSpan.FromMilliseconds(300) };

        // 1 kB/s with 16 kB chunks means the first chunk arrives after 16 s; the stall timeout fires long before.
        await Downloader().DownloadAsync(new DownloadRequest(server.FileUri) { Options = options }, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
    }

    [Fact]
    public async Task NotEnoughFreeSpaceIsReportedBeforeAnythingIsWritten()
    {
        using var dir = new TempDirectory();
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1]) };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-0/109951162777600");
            return response;
        });
        var downloader = new SegmentedDownloader(handlerFactory: _ => handler);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => downloader.DownloadAsync(
            new DownloadRequest(new Uri("https://example.org/huge.iso")) { Options = FastOptions() },
            dir.File("huge.iso")));

        Assert.Equal(ErrorCode.InsufficientSpace, ex.Code);
        Assert.Equal("100.0 TiB", ex.Arguments[1]);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task ErrorDescriptionIsAvailableInBothLanguages()
    {
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(RandomBytes(1000));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            new DownloadRequest(new Uri(server.BaseAddress, "/missing")) { Options = FastOptions() },
            dir.File("a.bin")));

        var german = ErrorCatalog.Describe(ex, new Core.Localization.Localizer { Culture = new System.Globalization.CultureInfo("de") });
        var english = ErrorCatalog.Describe(ex, new Core.Localization.Localizer { Culture = new System.Globalization.CultureInfo("en") });
        Assert.Contains("HTTP 404", german.Cause, StringComparison.Ordinal);
        Assert.Contains("HTTP 404", english.Cause, StringComparison.Ordinal);
        Assert.Equal("BX5001", english.Code);
    }
}
