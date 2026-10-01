// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using static Bootrix.Core.Tests.Net.Support.DownloadTestSupport;

namespace Bootrix.Core.Tests.Net;

public class SegmentedDownloaderMirrorTests
{
    private static DownloadRequest Over(DownloadOptions options, params MirrorSource[] sources) =>
        new(sources) { Options = options };

    [Fact]
    public async Task SegmentsAreSpreadOverAllMirrors()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var a = await FileServer.StartAsync(content);
        await using var b = await FileServer.StartAsync(content);

        var result = await Downloader().DownloadAsync(
            Over(FastOptions(), new MirrorSource(a.FileUri), new MirrorSource(b.FileUri)),
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(Sha256Hex(content), result.Sha256);
        Assert.True(a.BytesSent > content.Length / 5, $"first mirror delivered only {a.BytesSent}");
        Assert.True(b.BytesSent > content.Length / 5, $"second mirror delivered only {b.BytesSent}");
    }

    [Fact]
    public async Task ConnectionLimitOfAMirrorIsRespected()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var limited = await FileServer.StartAsync(content);
        await using var open = await FileServer.StartAsync(content);
        limited.Script = _ => Fault.Throttle(3_000_000);
        open.Script = _ => Fault.Throttle(3_000_000);

        await Downloader().DownloadAsync(
            Over(FastOptions(maxSegments: 6), new MirrorSource(limited.FileUri, MaxConnections: 1), new MirrorSource(open.FileUri)),
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(1, limited.PeakConcurrency);
        Assert.True(open.PeakConcurrency >= 2);
    }

    [Fact]
    public async Task MirrorWithADifferentLengthIsIgnored()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        await using var good = await FileServer.StartAsync(content);
        await using var other = await FileServer.StartAsync(RandomBytes(4 * MiB + 1, seed: 9));

        await Downloader().DownloadAsync(
            Over(FastOptions(), new MirrorSource(good.FileUri), new MirrorSource(other.FileUri)),
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(1, other.RequestCount);
    }

    [Fact]
    public async Task MirrorWithoutRangeSupportIsIgnoredWhenOthersHaveIt()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        await using var good = await FileServer.StartAsync(content);
        await using var plain = await FileServer.StartAsync(content);
        plain.SupportsRanges = false;

        await Downloader().DownloadAsync(
            Over(FastOptions(), new MirrorSource(plain.FileUri), new MirrorSource(good.FileUri)),
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(1, plain.RequestCount);
    }

    [Fact]
    public async Task UnreachableMirrorDoesNotStopTheDownload()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        await using var good = await FileServer.StartAsync(content);
        await using var broken = await FileServer.StartAsync(content);
        broken.Script = _ => Fault.Status(500);

        await Downloader().DownloadAsync(
            Over(FastOptions() with { MaxRetries = 2 }, new MirrorSource(good.FileUri), new MirrorSource(broken.FileUri)),
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(3, broken.RequestCount);
    }

    [Fact]
    public async Task MirrorTakesOverWhenThePrimarySourceIsGone()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        await using var gone = await FileServer.StartAsync(content);
        await using var good = await FileServer.StartAsync(content);
        var missing = new Uri(gone.BaseAddress, "/not-here");

        var result = await Downloader().DownloadAsync(
            Over(FastOptions(), new MirrorSource(missing), new MirrorSource(good.FileUri)),
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(good.FileUri, result.FinalUrl);
    }

    [Fact]
    public async Task ErrorOfThePrimaryIsReportedWhenNoSourceWorks()
    {
        using var dir = new TempDirectory();
        await using var a = await FileServer.StartAsync(RandomBytes(1000));
        await using var b = await FileServer.StartAsync(RandomBytes(1000));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(
            Over(FastOptions(), new MirrorSource(new Uri(a.BaseAddress, "/x")), new MirrorSource(new Uri(b.BaseAddress, "/y"))),
            dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Equal(["HTTP 404"], ex.Arguments);
    }

    [Fact]
    public async Task MirrorThatKeepsDroppingConnectionsIsReplacedByTheOthers()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var good = await FileServer.StartAsync(content);
        await using var flaky = await FileServer.StartAsync(content);
        flaky.Script = info => info.RangeStart > 0 ? Fault.AbortAfter(0) : Fault.None;

        await Downloader().DownloadAsync(
            Over(FastOptions() with { MaxRetries = 2 }, new MirrorSource(good.FileUri), new MirrorSource(flaky.FileUri)),
            dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
    }

    [Fact]
    public async Task CorruptPiecesFromOneMirrorAreFetchedFromAnotherAndTheMirrorIsDropped()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var good = await FileServer.StartAsync(content);
        await using var bad = await FileServer.StartAsync(content);
        bad.Script = _ => Fault.Garble();

        var request = Over(FastOptions(), new MirrorSource(bad.FileUri), new MirrorSource(good.FileUri)) with
        {
            Pieces = PiecesOf(content, MiB),
            ExpectedHashes = HashesOf(content),
        };

        var result = await Downloader().DownloadAsync(request, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(Sha256Hex(content), result.Sha256);
        Assert.True(good.BytesSent >= content.Length / 2, "the honest mirror has to supply what the other one corrupted");
    }

    [Fact]
    public async Task OnePieceCorruptedOnceIsRepairedFromTheSameServer()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = info => info.Index is 2 ? Fault.Garble() : Fault.None;
        var request = new DownloadRequest(server.FileUri)
        {
            Options = FastOptions(),
            Pieces = PiecesOf(content, MiB),
            ExpectedHashes = HashesOf(content),
        };

        await Downloader().DownloadAsync(request, dir.File("a.bin"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.True(server.RequestCount > 5, "the corrupt piece should have been requested again");
    }

    [Fact]
    public async Task PiecesThatNeverMatchEndInAHashMismatchAndLeaveNoFiles()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.Script = _ => Fault.Garble();
        var request = new DownloadRequest(server.FileUri) { Options = FastOptions(), Pieces = PiecesOf(content, MiB) };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader().DownloadAsync(request, dir.File("a.bin")));

        Assert.Equal(ErrorCode.DownloadHashMismatch, ex.Code);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task PiecesThatDoNotCoverTheAnnouncedSizeAreRejectedUpFront()
    {
        var content = RandomBytes(4 * MiB);
        using var dir = new TempDirectory();
        var request = new DownloadRequest(new Uri("http://localhost/x"))
        {
            ExpectedSize = content.Length + 1,
            Pieces = PiecesOf(content, MiB),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Downloader().DownloadAsync(request, dir.File("a.bin")));
    }

    [Fact]
    public async Task MetalinkDrivesAMultiMirrorDownloadWithPieceChecks()
    {
        var content = RandomBytes(6 * MiB + 321);
        using var dir = new TempDirectory();
        await using var first = await FileServer.StartAsync(content);
        await using var second = await FileServer.StartAsync(content);
        var pieces = PiecesOf(content, MiB);

        var xml = $"""
            <metalink xmlns="urn:ietf:params:xml:ns:metalink">
              <file name="image.iso">
                <size>{content.Length}</size>
                <hash type="sha-256">{Sha256Hex(content)}</hash>
                <pieces length="{MiB}" type="sha-1">{string.Concat(pieces.Select(p => $"<hash>{p.Hash.Hex}</hash>"))}</pieces>
                <url location="DE" priority="2">{second.FileUri}</url>
                <url location="US" priority="1">{first.FileUri}</url>
              </file>
            </metalink>
            """;
        var file = MetalinkDocument.Parse(xml).Files.Single();

        var result = await Downloader().DownloadAsync(file.ToRequest(options: FastOptions()), dir.File("image.iso"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("image.iso")));
        Assert.Equal(Sha256Hex(content), result.Sha256);
        Assert.True(first.BytesSent > 0 && second.BytesSent > 0);
    }

    [Fact]
    public async Task MetalinkMaxConnectionsOfOneIsHonouredPerMirror()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var a = await FileServer.StartAsync(content);
        await using var b = await FileServer.StartAsync(content);
        a.Script = _ => Fault.Throttle(4_000_000);
        b.Script = _ => Fault.Throttle(4_000_000);

        var xml = $"""
            <metalink version="3.0" xmlns="http://www.metalinker.org/"><files><file name="x.iso">
              <size>{content.Length}</size>
              <verification><hash type="sha256">{Sha256Hex(content)}</hash></verification>
              <resources maxconnections="1">
                <url type="http" preference="100">{a.FileUri}</url>
                <url type="http" preference="99">{b.FileUri}</url>
              </resources>
            </file></files></metalink>
            """;

        await Downloader().DownloadAsync(MetalinkDocument.Parse(xml).Files.Single().ToRequest(options: FastOptions(maxSegments: 6)), dir.File("x.iso"));

        Assert.Equal(content, await File.ReadAllBytesAsync(dir.File("x.iso")));
        Assert.Equal(1, a.PeakConcurrency);
        Assert.Equal(1, b.PeakConcurrency);
    }
}
