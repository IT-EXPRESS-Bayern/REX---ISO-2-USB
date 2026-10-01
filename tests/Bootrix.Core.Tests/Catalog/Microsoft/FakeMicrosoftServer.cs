// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using System.Web;
using Bootrix.Core.Tests.Net.Support;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

/// <summary>
/// Plays Microsoft's side of the exchange with the recorded answers: download pages, the fingerprinting script and
/// ping, the connector API. A session may only receive links after its ping, as the real service insists, and
/// every address it issues carries a fresh token so that renewals are visible.
/// </summary>
internal sealed class FakeMicrosoftServer
{
    private const string Placeholder = "00000000-0000-0000-0000-000000000000";

    private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["windows11"] = "page-windows11.html",
        ["windows11arm64"] = "page-windows11arm64.html",
        ["windows10ISO"] = "page-windows10iso.html",
    };

    private static readonly Dictionary<string, string> SkuLists = new()
    {
        ["3813"] = "skus-3813.json",
        ["3816"] = "skus-3816.json",
        ["2618"] = "skus-2618.json",
    };

    // The German entry of each list and the recorded link answer that belongs to it.
    private static readonly Dictionary<string, string> LinkAnswers = new()
    {
        ["27126"] = "links-3813-de.json",
        ["27167"] = "links-3816-de.json",
        ["16073"] = "links-2618-de.json",
    };

    private int _issued;

    public List<HttpRequestMessage> Calls { get; } = [];

    public HashSet<string> Handshaken { get; } = [];

    public HashSet<string> Tagged { get; } = [];

    /// <summary>The list of languages already answers "rejected" until the session did its ping.</summary>
    public bool SkusNeedHandshake { get; set; }

    /// <summary>Links are only given to sessions that registered the profiling tag as well.</summary>
    public bool LinksNeedTag { get; set; }

    /// <summary>Refuses every link request, whatever the session did.</summary>
    public bool RejectAllLinks { get; set; }

    /// <summary>Gets the first look at each request; a non-null answer replaces the normal one.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public IEnumerable<string> Urls => Calls.Select(c => c.RequestUri!.ToString());

    public HttpClient Client() => new(new StubHandler(Handle));

    public IEnumerable<HttpRequestMessage> Api(string operation) =>
        Calls.Where(c => c.RequestUri!.AbsolutePath.EndsWith("/" + operation, StringComparison.OrdinalIgnoreCase));

    public static string Query(HttpRequestMessage request, string name) =>
        HttpUtility.ParseQueryString(request.RequestUri!.Query)[name] ?? string.Empty;

    public static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/html") };

    private HttpResponseMessage Handle(HttpRequestMessage request)
    {
        Calls.Add(request);

        if (Override?.Invoke(request) is { } custom)
        {
            return custom;
        }

        var uri = request.RequestUri!;
        var session = Query(request, "session_id") is { Length: > 0 } fromQuery ? fromQuery : Query(request, "sessionID");

        switch (uri.Host)
        {
            case "www.microsoft.com" when uri.AbsolutePath.StartsWith("/en-us/software-download/", StringComparison.Ordinal):
                return Pages.TryGetValue(uri.Segments[^1], out var page) ? Html(MicrosoftFixtures.Text(page)) : new HttpResponseMessage(HttpStatusCode.NotFound);

            case "www.microsoft.com" when uri.AbsolutePath.EndsWith("/getskuinformationbyproductedition", StringComparison.OrdinalIgnoreCase):
                if (SkusNeedHandshake && !Handshaken.Contains(session))
                {
                    return Json(MicrosoftFixtures.Text("links-rejected.json"));
                }

                return SkuLists.TryGetValue(Query(request, "ProductEditionId"), out var skus) ? Json(MicrosoftFixtures.Text(skus)) : new HttpResponseMessage(HttpStatusCode.NotFound);

            case "www.microsoft.com" when uri.AbsolutePath.EndsWith("/GetProductDownloadLinksBySku", StringComparison.OrdinalIgnoreCase):
                if (RejectAllLinks || !Handshaken.Contains(session) || (LinksNeedTag && !Tagged.Contains(session)))
                {
                    return Json(MicrosoftFixtures.Text("links-rejected.json"));
                }

                return Json(LinkAnswers.TryGetValue(Query(request, "SKU"), out var answer)
                    ? MicrosoftFixtures.Text(answer).Replace("t=" + Placeholder, $"t={++_issued:D8}-0000-0000-0000-000000000000", StringComparison.Ordinal)
                    : """{"ProductDownloadOptions":[],"ValidationContainer":{"ErrorList":[],"Errors":[]}}""");

            case "ov-df.microsoft.com" when uri.AbsolutePath.EndsWith("/mdt.js", StringComparison.Ordinal):
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(MicrosoftFixtures.Text("ov-df-mdt.js").Replace(Placeholder, session, StringComparison.Ordinal), Encoding.UTF8, "application/javascript"),
                };

            case "ov-df.microsoft.com":
                Handshaken.Add(session);
                return Html("<html><body>fingerprint page</body></html>");

            case "vlscppe.microsoft.com":
                Tagged.Add(session);
                return Html(MicrosoftFixtures.Text("vlscppe-tags.html").Replace(Placeholder, session, StringComparison.Ordinal));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
