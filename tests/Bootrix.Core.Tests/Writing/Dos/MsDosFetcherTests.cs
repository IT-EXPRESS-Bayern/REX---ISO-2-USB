// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Writing.Dos;

/// <summary>The download logic without a network: a handler plays the symbol server, and a stand-in file has the pinned size and hash.</summary>
public sealed class MsDosFetcherTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "bootrix-msdos-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _dll = MsDosFixtures.Dll(MsDosFixtures.Floppy());

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    private sealed class FixedVerifier(bool signed) : IMicrosoftSignatureVerifier
    {
        public int Calls { get; private set; }

        public bool IsSignedByMicrosoft(string path)
        {
            Calls++;
            return signed;
        }
    }

    private sealed class ServerHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private MsDosSource Source(byte[]? content = null)
    {
        content ??= _dll;
        return new MsDosSource(new Uri("https://symbols.test/diskcopy.dll"), content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    private static HttpResponseMessage Ok(byte[] content) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

    private MsDosFetcher Fetcher(ServerHandler handler, IMicrosoftSignatureVerifier verifier, MsDosSource? source = null) =>
        new(new HttpClient(handler), verifier, _cache, source ?? Source());

    [Fact]
    public void ThePinnedSourceIsTheOneInSources()
    {
        Assert.Equal(0x16EE00, MsDosSource.Microsoft.Length);
        Assert.Equal("95fc0786f5bc0a6db5c0604b31ac18fbed0502a2c6858e5fb02a647983ae03c7", MsDosSource.Microsoft.Sha256);
        Assert.Equal("msdl.microsoft.com", MsDosSource.Microsoft.Url.Host);
        Assert.Equal("https", MsDosSource.Microsoft.Url.Scheme);
    }

    [Fact]
    public async Task WithoutTheUsersConfirmation_NothingIsDownloaded()
    {
        var server = new ServerHandler(_ => Ok(_dll));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Fetcher(server, new FixedVerifier(true)).GetAsync(false, null, CancellationToken.None));

        Assert.Equal(ErrorCode.MsDosNotDownloaded, ex.Code);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AConfirmedDownload_IsCheckedKeptAndRequestedLikeTheDebuggerDoes()
    {
        var server = new ServerHandler(_ => Ok(_dll));
        var verifier = new FixedVerifier(true);
        var reports = new List<double>();

        var content = await Fetcher(server, verifier).GetAsync(true, new ImmediateProgress(reports.Add), CancellationToken.None);

        Assert.Equal(_dll, content);
        Assert.Equal(_dll, await File.ReadAllBytesAsync(Path.Combine(_cache, "diskcopy.dll")));
        Assert.Equal(1, verifier.Calls);
        var request = Assert.Single(server.Requests);
        Assert.Equal(MsDosFetcher.UserAgent, request.Headers.UserAgent.ToString());
        Assert.Equal("https://symbols.test/diskcopy.dll", request.RequestUri!.ToString());
        Assert.InRange(reports.Max(), 0.99, 1.0);
        Assert.Empty(Directory.GetFiles(_cache, "*.download"));
    }

    [Fact]
    public async Task ACachedFile_IsUsedWithoutTheNetwork_EvenWithoutConfirmation()
    {
        var first = new ServerHandler(_ => Ok(_dll));
        await Fetcher(first, new FixedVerifier(true)).GetAsync(true, null, CancellationToken.None);
        var second = new ServerHandler(_ => throw new InvalidOperationException("no request expected"));

        var content = await Fetcher(second, new FixedVerifier(true)).GetAsync(false, null, CancellationToken.None);

        Assert.Equal(_dll, content);
        Assert.Empty(second.Requests);
    }

    [Fact]
    public async Task ACachedFileThatWasChanged_IsDiscardedAndFetchedAgain_OrRefusedWithoutConfirmation()
    {
        var server = new ServerHandler(_ => Ok(_dll));
        await Fetcher(server, new FixedVerifier(true)).GetAsync(true, null, CancellationToken.None);
        var cached = Path.Combine(_cache, "diskcopy.dll");
        var tampered = (byte[])_dll.Clone();
        tampered[100] ^= 0xFF;
        await File.WriteAllBytesAsync(cached, tampered);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Fetcher(server, new FixedVerifier(true)).GetAsync(false, null, CancellationToken.None));
        Assert.Equal(ErrorCode.MsDosNotDownloaded, ex.Code);
        Assert.False(File.Exists(cached));

        var again = await Fetcher(server, new FixedVerifier(true)).GetAsync(true, null, CancellationToken.None);
        Assert.Equal(_dll, again);
    }

    [Fact]
    public async Task AFileWithTheWrongHash_IsRejectedAndNotKept()
    {
        var other = (byte[])_dll.Clone();
        other[200] ^= 0xFF;
        var server = new ServerHandler(_ => Ok(other));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Fetcher(server, new FixedVerifier(true)).GetAsync(true, null, CancellationToken.None));

        Assert.Equal(ErrorCode.MsDosFilesUntrusted, ex.Code);
        Assert.Empty(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task AFileWithTheWrongSize_IsRejected()
    {
        var server = new ServerHandler(_ => Ok(_dll[..^1]));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Fetcher(server, new FixedVerifier(true)).GetAsync(true, null, CancellationToken.None));

        Assert.Equal(ErrorCode.MsDosFilesUntrusted, ex.Code);
        Assert.Empty(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task AFileThatIsLargerThanAnnounced_IsCutOffWhileReading()
    {
        var server = new ServerHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream([.. _dll, .. new byte[10_000]])),
        });

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Fetcher(server, new FixedVerifier(true)).GetAsync(true, null, CancellationToken.None));

        Assert.Equal(ErrorCode.MsDosFilesUntrusted, ex.Code);
        Assert.Empty(Directory.GetFiles(_cache));
    }

    [Fact]
    public async Task AFileWithoutMicrosoftSignature_IsRejectedEvenWhenTheHashMatches()
    {
        var server = new ServerHandler(_ => Ok(_dll));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Fetcher(server, new FixedVerifier(false)).GetAsync(true, null, CancellationToken.None));

        Assert.Equal(ErrorCode.MsDosFilesUntrusted, ex.Code);
        Assert.Empty(Directory.GetFiles(_cache));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AnHttpError_IsReportedAsAFailedDownload(HttpStatusCode status)
    {
        var server = new ServerHandler(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Fetcher(server, new FixedVerifier(true)).GetAsync(true, null, CancellationToken.None));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
    }

    [Fact]
    public async Task Cancellation_StopsTheDownload_AndLeavesNoPartialFile()
    {
        var server = new ServerHandler(_ => Ok(_dll));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetcher(server, new FixedVerifier(true)).GetAsync(true, null, cancelled.Token));

        Assert.False(Directory.Exists(_cache) && Directory.GetFiles(_cache).Length > 0);
    }

    private sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
