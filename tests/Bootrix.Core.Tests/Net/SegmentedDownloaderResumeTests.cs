// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using static Bootrix.Core.Tests.Net.Support.DownloadTestSupport;

namespace Bootrix.Core.Tests.Net;

public class SegmentedDownloaderResumeTests
{
    private static readonly SegmentedDownloader Downloader = DownloadTestSupport.Downloader();

    /// <summary>Starts a throttled download and cancels it as soon as <paramref name="cancelAt"/> bytes are on disk.</summary>
    private static async Task InterruptAsync(FileServer server, string destination, long cancelAt, DownloadRequest? request = null, DownloadPhase phase = DownloadPhase.Downloading)
    {
        using var cts = new CancellationTokenSource();
        var progress = new ProgressLog
        {
            OnReport = p =>
            {
                if (p.Phase == phase && p.BytesDone >= cancelAt)
                {
                    cts.Cancel();
                }
            },
        };

        request ??= new DownloadRequest(server.FileUri) { Options = FastOptions() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Downloader.DownloadAsync(request, destination, progress, cts.Token));
    }

    private static List<(long Start, long End)> SavedRanges(string statePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(statePath));
        return [.. document.RootElement.GetProperty("ranges").EnumerateArray().Select(r => (r.GetProperty("start").GetInt64(), r.GetProperty("end").GetInt64()))];
    }

    private static bool IsProbe(RequestRecord request) => request is { RangeStart: 0, RangeEnd: 0 };

    private static void Throttled(FileServer server) => server.Script = _ => Fault.Throttle(2_000_000);

    [Fact]
    public async Task CancelledDownloadResumesWithoutFetchingFinishedRangesAgain()
    {
        var content = RandomBytes(8 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        Throttled(server);
        var path = dir.File("a.bin");

        await InterruptAsync(server, path, 3 * MiB);

        Assert.False(File.Exists(path));
        Assert.Equal(content.Length, new FileInfo(path + ".part").Length);
        var saved = SavedRanges(path + ".btxdl");
        var savedBytes = saved.Sum(r => r.End - r.Start);
        Assert.InRange(savedBytes, 3 * MiB - 512 * 1024, content.Length - MiB);

        server.Script = null;
        var marker = server.RequestCount;
        var result = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);

        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.Equal(savedBytes, result.ResumedBytes);
        Assert.False(File.Exists(path + ".part"));
        Assert.False(File.Exists(path + ".btxdl"));

        var secondRun = server.Requests.Where(r => r.Index > marker && !IsProbe(r)).ToList();
        Assert.NotEmpty(secondRun);
        Assert.All(secondRun, r => Assert.DoesNotContain(saved, s => s.Start <= r.RangeStart && r.RangeStart < s.End));
    }

    [Fact]
    public async Task ResumedRequestsAreConditionalOnTheValidator()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        Throttled(server);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 2 * MiB);

        server.Script = null;
        var marker = server.RequestCount;
        await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);

        var segments = server.Requests.Where(r => r.Index > marker && !IsProbe(r)).ToList();
        Assert.NotEmpty(segments);
        Assert.All(segments, r => Assert.True(r.HasIfRange));
    }

    [Fact]
    public async Task StateFileIsRewrittenWhileTheDownloadRuns()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        Throttled(server);
        var path = dir.File("a.bin");
        var seenRanges = 0L;
        var progress = new ProgressLog
        {
            OnReport = p =>
            {
                if (p.Phase != DownloadPhase.Downloading || !File.Exists(path + ".btxdl"))
                {
                    return;
                }

                try
                {
                    seenRanges = Math.Max(seenRanges, SavedRanges(path + ".btxdl").Sum(r => r.End - r.Start));
                }
                catch (IOException)
                {
                    // Replaced at that very moment; the next report sees the new file.
                }
            },
        };

        await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path, progress);

        Assert.True(seenRanges > MiB, $"state file never showed more than {seenRanges} bytes");
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ChangedETagStartsOverCleanly()
    {
        var oldContent = RandomBytes(6 * MiB, seed: 1);
        var newContent = RandomBytes(6 * MiB, seed: 2);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(oldContent);
        Throttled(server);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 2 * MiB);

        server.Script = null;
        server.Content = newContent;
        server.ETag = "\"v2\"";
        var result = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);

        Assert.Equal(0, result.ResumedBytes);
        Assert.Equal(newContent, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ChangedLengthStartsOverCleanly()
    {
        var oldContent = RandomBytes(6 * MiB, seed: 1);
        var newContent = RandomBytes(5 * MiB, seed: 2);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(oldContent);
        Throttled(server);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 2 * MiB);

        server.Script = null;
        server.Content = newContent;
        var result = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);

        Assert.Equal(0, result.ResumedBytes);
        Assert.Equal(newContent, await File.ReadAllBytesAsync(path));
        Assert.Equal(newContent.Length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task LastModifiedIsTheValidatorWhenThereIsNoETag()
    {
        var oldContent = RandomBytes(6 * MiB, seed: 1);
        var newContent = RandomBytes(6 * MiB, seed: 2);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(oldContent);
        server.ETag = null;
        Throttled(server);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 2 * MiB);

        // Unchanged Last-Modified: resumes.
        server.Script = null;
        var resumed = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);
        Assert.True(resumed.ResumedBytes > 0);
        Assert.Equal(oldContent, await File.ReadAllBytesAsync(path));

        // Changed Last-Modified: starts over.
        Throttled(server);
        await InterruptAsync(server, path, 2 * MiB);
        server.Script = null;
        server.Content = newContent;
        server.LastModified = server.LastModified.AddDays(1);
        var restarted = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);
        Assert.Equal(0, restarted.ResumedBytes);
        Assert.Equal(newContent, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task WithoutAnyValidatorOnlyTheSameAddressResumes()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        server.ETag = null;
        server.SendLastModified = false;
        server.Redirects["/alias"] = "/file";
        Throttled(server);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 2 * MiB);

        server.Script = null;
        var otherAddress = await Downloader.DownloadAsync(
            new DownloadRequest(new Uri(server.BaseAddress, "/alias")) { Options = FastOptions() },
            path);
        Assert.Equal(0, otherAddress.ResumedBytes);

        Throttled(server);
        await InterruptAsync(server, path, 2 * MiB);
        server.Script = null;
        var sameAddress = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);
        Assert.True(sameAddress.ResumedBytes > 0);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("{\"version\":1,\"url\":\"x\",\"length\":6291456,\"ranges\":[{\"start\":0,\"end\":999999999}]}")]
    [InlineData("{\"version\":1,\"url\":\"x\",\"length\":6291456,\"ranges\":[{\"start\":5,\"end\":5}]}")]
    [InlineData("{\"version\":99,\"url\":\"x\",\"length\":6291456,\"ranges\":[]}")]
    public async Task UnusableStateFileMeansAFreshStart(string state)
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        var path = dir.File("a.bin");
        await File.WriteAllBytesAsync(path + ".part", new byte[content.Length]);
        await File.WriteAllTextAsync(path + ".btxdl", state);

        var result = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);

        Assert.Equal(0, result.ResumedBytes);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task PartFileOfTheWrongSizeIsNotTrusted()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        Throttled(server);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 2 * MiB);
        await using (var part = new FileStream(path + ".part", FileMode.Open, FileAccess.Write))
        {
            part.SetLength(content.Length - 10);
        }

        server.Script = null;
        var result = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);

        Assert.Equal(0, result.ResumedBytes);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task StaleStateIsNeverAppliedToAFreshPartFile()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        Throttled(server);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 2 * MiB);
        File.Delete(path + ".part");

        server.Script = null;
        var result = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);

        Assert.Equal(0, result.ResumedBytes);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InterruptionDuringVerificationLeavesNothingToDownloadAgain()
    {
        // Large enough that hashing outlasts a progress interval and the cancellation lands inside it.
        var content = RandomBytes(64 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 0, phase: DownloadPhase.Verifying);

        var saved = SavedRanges(path + ".btxdl");
        Assert.Equal([(0L, (long)content.Length)], saved);

        var marker = server.RequestCount;
        var result = await Downloader.DownloadAsync(new DownloadRequest(server.FileUri) { Options = FastOptions() }, path);

        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.Equal(content.Length, result.ResumedBytes);
        Assert.Equal(1, server.RequestCount - marker);
    }

    [Fact]
    public async Task PieceDigestsCatchCorruptionInTheLeftoverPartFile()
    {
        var content = RandomBytes(8 * MiB);
        var pieces = PiecesOf(content, MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        Throttled(server);
        var path = dir.File("a.bin");
        var request = new DownloadRequest(server.FileUri) { Options = FastOptions(), Pieces = pieces };
        await InterruptAsync(server, path, 4 * MiB, request);

        var saved = SavedRanges(path + ".btxdl");
        var intact = pieces.First(p => saved.Any(r => r.Start <= p.Offset && p.End <= r.End));
        await using (var part = new FileStream(path + ".part", FileMode.Open, FileAccess.ReadWrite))
        {
            part.Position = intact.Offset + 1000;
            var original = part.ReadByte();
            part.Position = intact.Offset + 1000;
            part.WriteByte((byte)(original ^ 0xFF));
        }

        server.Script = null;
        var marker = server.RequestCount;
        var result = await Downloader.DownloadAsync(request, path);

        Assert.Equal(content, await File.ReadAllBytesAsync(path));
        Assert.Contains(server.Requests, r => r.Index > marker && r.RangeStart == intact.Offset);
        Assert.True(result.ResumedBytes > 0);
    }

    [Fact]
    public async Task CorruptLeftoverWithoutPiecesIsCaughtByTheFinalDigestAndThrownAway()
    {
        var content = RandomBytes(6 * MiB);
        using var dir = new TempDirectory();
        await using var server = await FileServer.StartAsync(content);
        Throttled(server);
        var path = dir.File("a.bin");
        await InterruptAsync(server, path, 3 * MiB);
        await using (var part = new FileStream(path + ".part", FileMode.Open, FileAccess.ReadWrite))
        {
            part.Position = 10;
            part.WriteByte(0x42);
            part.WriteByte(0x43);
        }

        server.Script = null;
        var request = new DownloadRequest(server.FileUri) { Options = FastOptions(), ExpectedHashes = HashesOf(content) };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Downloader.DownloadAsync(request, path));

        Assert.Equal(ErrorCode.DownloadHashMismatch, ex.Code);
        Assert.Empty(Directory.GetFiles(dir.Path));

        var retry = await Downloader.DownloadAsync(request, path);
        Assert.Equal(0, retry.ResumedBytes);
        Assert.Equal(content, await File.ReadAllBytesAsync(path));
    }
}
