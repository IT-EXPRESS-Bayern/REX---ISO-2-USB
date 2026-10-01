// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Net.Support;

/// <summary>Answers requests from a delegate, without any socket; records what was asked.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(Uri Uri, HttpRequestMessage Request)> Calls { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls.Add((request.RequestUri!, request));
        return Task.FromResult(respond(request));
    }
}
