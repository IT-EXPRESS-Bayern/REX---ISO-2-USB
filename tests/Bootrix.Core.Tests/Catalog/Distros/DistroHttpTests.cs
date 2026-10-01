// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class DistroHttpTests
{
    private const string Url = "https://vendor.example/list.txt";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private DistroHttp Http(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public async Task Answers_AreKeptForTenMinutes()
    {
        var web = new FakeWeb().Serve(Url, "one");
        var http = Http(web);

        Assert.Equal("one", await http.GetStringAsync(new Uri(Url), CancellationToken.None));
        web.Serve(Url, "two");
        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal("one", await http.GetStringAsync(new Uri(Url), CancellationToken.None));

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("two", await http.GetStringAsync(new Uri(Url), CancellationToken.None));
        Assert.Equal(2, web.Count(Url));
    }

    [Fact]
    public async Task ZeroLifetime_AlwaysAsksTheServerAndLeavesNoCopy()
    {
        var web = new FakeWeb().Serve(Url, "one");
        var http = Http(web);
        await http.GetStringAsync(new Uri(Url), CancellationToken.None);

        web.Serve(Url, "two");

        Assert.Equal("two", await http.GetStringAsync(new Uri(Url), CancellationToken.None, TimeSpan.Zero));
        Assert.Equal("one", await http.GetStringAsync(new Uri(Url), CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ErrorStatus_IsCatalogUnavailableAndNamesTheAddress(HttpStatusCode status)
    {
        var http = Http(new FakeWeb().Fail(Url, status));

        var error = await Assert.ThrowsAsync<BootrixException>(() => http.GetBytesAsync(new Uri(Url), CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains(Url, error.Detail, StringComparison.Ordinal);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryGet_TurnsNotFoundIntoNullButNotOtherErrors()
    {
        var web = new FakeWeb().Fail("https://vendor.example/gone", HttpStatusCode.Gone).Fail("https://vendor.example/broken", HttpStatusCode.BadGateway);
        var http = Http(web);

        Assert.Null(await http.TryGetBytesAsync(new Uri("https://vendor.example/missing"), CancellationToken.None));
        Assert.Null(await http.TryGetStringAsync(new Uri("https://vendor.example/gone"), CancellationToken.None));
        await Assert.ThrowsAsync<BootrixException>(() => http.TryGetBytesAsync(new Uri("https://vendor.example/broken"), CancellationToken.None));
    }

    [Fact]
    public async Task ConnectionFailure_IsCatalogUnavailable()
    {
        var http = Http(new FakeWeb().Throw(Url, new HttpRequestException("no route to host")));

        var error = await Assert.ThrowsAsync<BootrixException>(() => http.GetBytesAsync(new Uri(Url), CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task Timeout_IsCatalogUnavailable_ButCancellationByTheCallerIsNot()
    {
        var timeout = Http(new FakeWeb().Throw(Url, new TaskCanceledException("timed out")));
        var error = await Assert.ThrowsAsync<BootrixException>(() => timeout.GetBytesAsync(new Uri(Url), CancellationToken.None));
        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelled = Http(new FakeWeb().Serve(Url, "x"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.GetBytesAsync(new Uri(Url), cts.Token));
    }

    [Fact]
    public async Task Text_DropsAByteOrderMark()
    {
        var http = Http(new FakeWeb().Serve(Url, [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b']));

        Assert.Equal("ab", await http.GetStringAsync(new Uri(Url), CancellationToken.None));
    }

    [Fact]
    public async Task Requests_IdentifyBootrixToTheServer()
    {
        string? agent = null;
        var handler = new StubHandler(request =>
        {
            agent = request.Headers.UserAgent.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        });
        var http = new DistroHttp(new HttpClient(handler), _time);

        await http.GetStringAsync(new Uri(Url), CancellationToken.None);

        Assert.StartsWith("Bootrix/", agent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedAnswer_IsRejectedInsteadOfBuffered()
    {
        var handler = new StubHandler(_ =>
        {
            var content = new StreamContent(new MemoryStream(new byte[1]));
            content.Headers.ContentLength = 17 * 1024 * 1024;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var http = new DistroHttp(new HttpClient(handler), _time);

        var error = await Assert.ThrowsAsync<BootrixException>(() => http.GetBytesAsync(new Uri(Url), CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }
}
