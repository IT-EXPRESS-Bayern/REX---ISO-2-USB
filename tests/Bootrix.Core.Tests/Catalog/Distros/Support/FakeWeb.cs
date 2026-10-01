// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;

namespace Bootrix.Core.Tests.Catalog.Distros.Support;

/// <summary>
/// A vendor site without a network: answers GET requests from a table of URL to body, 404 for anything else, and
/// remembers what was asked.
/// </summary>
internal sealed class FakeWeb : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<Uri> Requests { get; } = [];

    public FakeWeb Serve(string url, byte[] body)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        return this;
    }

    public FakeWeb Serve(string url, string text) => Serve(url, System.Text.Encoding.UTF8.GetBytes(text));

    public FakeWeb ServeFixture(string url, string fixture) => Serve(url, DistroFixtures.Bytes(fixture));

    public FakeWeb Fail(string url, HttpStatusCode status)
    {
        _routes[url] = () => new HttpResponseMessage(status);
        return this;
    }

    public FakeWeb Throw(string url, Exception error)
    {
        _routes[url] = () => throw error;
        return this;
    }

    public int Count(string url) => Requests.Count(r => r.AbsoluteUri == url);

    public HttpClient CreateClient() => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route)
            ? route()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
