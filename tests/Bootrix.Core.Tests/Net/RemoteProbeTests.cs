// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;

namespace Bootrix.Core.Tests.Net;

public class RemoteProbeTests
{
    private static readonly DownloadOptions Options = new() { UserAgent = "Test/1.0" };

    private static HttpResponseMessage Partial(string contentRange, string? etag = "\"abc\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1]) };
        response.Content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        response.Content.Headers.LastModified = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
        return response;
    }

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private static Task<ProbeResult> ProbeAsync(StubHandler handler, string url = "https://example.org/a.iso", DownloadOptions? options = null)
    {
        var http = new HttpClient(handler);
        return RemoteProbe.ResolveAsync(http, new Uri(url), options ?? Options, CancellationToken.None);
    }

    [Fact]
    public async Task PartialContentGivesLengthFromContentRangeAndValidators()
    {
        var handler = new StubHandler(_ => Partial("bytes 0-0/4096"));

        using var probe = await ProbeAsync(handler);

        Assert.True(probe.RangeSupported);
        Assert.Equal(4096, probe.Length);
        Assert.Equal("\"abc\"", probe.ETag);
        Assert.Equal(new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero), probe.LastModified);
        Assert.Null(probe.Body);
    }

    [Fact]
    public async Task ProbeAsksForTheFirstByteOnly()
    {
        var handler = new StubHandler(_ => Partial("bytes 0-0/10"));

        using var probe = await ProbeAsync(handler);

        var request = Assert.Single(handler.Calls).Request;
        Assert.Equal(new RangeHeaderValue(0, 0), request.Headers.Range);
        Assert.Equal(new Version(1, 1), request.Version);
        Assert.Equal("identity", request.Headers.AcceptEncoding.ToString());
        Assert.Equal("Test/1.0", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task OkWithContentLengthMeansNoRangesAndKeepsTheBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[123]) });

        using var probe = await ProbeAsync(handler);

        Assert.False(probe.RangeSupported);
        Assert.Equal(123, probe.Length);
        Assert.NotNull(probe.Body);
    }

    [Fact]
    public async Task AcceptRangesAloneDoesNotCount()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
            response.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");
            return response;
        });

        using var probe = await ProbeAsync(handler);

        Assert.False(probe.RangeSupported);
    }

    [Fact]
    public async Task UnknownTotalInContentRangeMeansNoSegmentPlanning()
    {
        var handler = new StubHandler(_ => Partial("bytes 0-0/*"));

        using var probe = await ProbeAsync(handler);

        Assert.False(probe.RangeSupported);
        Assert.Null(probe.Length);
    }

    [Fact]
    public async Task UnsatisfiableRangeOnAnEmptyResourceMeansLengthZero()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */0");
            return response;
        });

        using var probe = await ProbeAsync(handler);

        Assert.Equal(0, probe.Length);
        Assert.False(probe.RangeSupported);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task EveryRedirectStatusIsFollowedAndTheFinalAddressReported(HttpStatusCode status)
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/a.iso" => Redirect(status, "/mirror/a.iso"),
            "/mirror/a.iso" => Redirect(status, "https://cdn.example.net/x/a.iso?sig=1"),
            _ => Partial("bytes 0-0/50"),
        });

        using var probe = await ProbeAsync(handler);

        Assert.Equal(new Uri("https://cdn.example.net/x/a.iso?sig=1"), probe.Resolved);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task RedirectFromHttpsToHttpIsRefused()
    {
        var handler = new StubHandler(_ => Redirect(HttpStatusCode.Found, "http://insecure.example.net/a.iso"));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ProbeAsync(handler));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task RedirectFromHttpToHttpsIsFine()
    {
        var handler = new StubHandler(request => request.RequestUri!.Scheme == "http"
            ? Redirect(HttpStatusCode.MovedPermanently, "https://example.org/a.iso")
            : Partial("bytes 0-0/9"));

        using var probe = await ProbeAsync(handler, "http://example.org/a.iso");

        Assert.Equal("https", probe.Resolved.Scheme);
    }

    [Theory]
    [InlineData("ftp://example.org/a.iso")]
    [InlineData("file:///etc/passwd")]
    public async Task RedirectToANonWebSchemeIsRefused(string location)
    {
        var handler = new StubHandler(_ => Redirect(HttpStatusCode.Found, location));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ProbeAsync(handler));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
    }

    [Fact]
    public async Task RedirectWithoutLocationIsAnError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Found));

        await Assert.ThrowsAsync<BootrixException>(() => ProbeAsync(handler));
    }

    [Fact]
    public async Task RedirectChainLongerThanTheLimitIsAnError()
    {
        var handler = new StubHandler(request => Redirect(HttpStatusCode.Found, request.RequestUri + "x"));

        await Assert.ThrowsAsync<BootrixException>(() => ProbeAsync(handler, options: Options with { MaxRedirects = 3 }));

        Assert.Equal(4, handler.Calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task ForbiddenAndGoneSignalAnExpiredLink(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<LinkExpiredException>(() => ProbeAsync(handler));

        Assert.Equal(status, ex.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task ServerErrorsAreTransient(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<TransientDownloadException>(() => ProbeAsync(handler));
    }

    [Fact]
    public async Task RetryAfterInSecondsIsCarriedOnTheTransientError()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        });

        var ex = await Assert.ThrowsAsync<TransientDownloadException>(() => ProbeAsync(handler));

        Assert.Equal(TimeSpan.FromSeconds(7), ex.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterAsDateIsConvertedToADelay()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30));
            return response;
        });

        var ex = await Assert.ThrowsAsync<TransientDownloadException>(() => ProbeAsync(handler));

        Assert.InRange(ex.RetryAfter!.Value, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(31));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task OtherClientErrorsArePermanent(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ProbeAsync(handler));

        Assert.Equal(ErrorCode.DownloadFailed, ex.Code);
        Assert.Equal([$"HTTP {(int)status}"], ex.Arguments);
    }

    [Fact]
    public async Task CustomHeadersAreSentButCredentialsDoNotLeaveTheirHost()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "example.org"
            ? Redirect(HttpStatusCode.Found, "https://cdn.example.net/a.iso")
            : Partial("bytes 0-0/9"));
        var options = Options with
        {
            Headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://example.org/page",
                ["Authorization"] = "Bearer secret",
            },
        };

        using var probe = await ProbeAsync(handler, options: options);

        var first = handler.Calls[0].Request;
        var second = handler.Calls[1].Request;
        Assert.Equal("Bearer secret", first.Headers.Authorization!.ToString());
        Assert.Equal("https://example.org/page", first.Headers.Referrer!.ToString());
        Assert.Null(second.Headers.Authorization);
        Assert.Equal("https://example.org/page", second.Headers.Referrer!.ToString());
    }

    [Fact]
    public async Task ResponseThatNeverArrivesBecomesATransientTimeout()
    {
        var handler = new HangingHandler();
        var http = new HttpClient(handler);

        await Assert.ThrowsAsync<TransientDownloadException>(() => RemoteProbe.ResolveAsync(
            http,
            new Uri("https://example.org/a"),
            Options with { ResponseTimeout = TimeSpan.FromMilliseconds(100) },
            CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsNotMistakenForATimeout()
    {
        using var cts = new CancellationTokenSource(50);
        var http = new HttpClient(new HangingHandler());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RemoteProbe.ResolveAsync(http, new Uri("https://example.org/a"), Options, cts.Token));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }
}
