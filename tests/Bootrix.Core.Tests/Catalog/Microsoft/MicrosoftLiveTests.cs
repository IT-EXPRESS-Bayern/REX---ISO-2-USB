// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

/// <summary>
/// Smoke tests against Microsoft's real servers, run only with <c>BOOTRIX_LIVE_TESTS=1</c>. Microsoft may refuse a
/// client it takes for automated (the 715-123130 answer): that is reported as an outcome of the test, not as a failure of
/// the code, because it depends on the network the test runs from.
/// </summary>
public class MicrosoftLiveTests(ITestOutputHelper output)
{
    private static readonly HttpClient Http = new();

    [LiveFact]
    public async Task Windows11Iso_GermanX64_ResolvesToAMicrosoftAddressThatServesRanges()
    {
        var provider = new MicrosoftIsoProvider(Http, NullLogger<MicrosoftIsoProvider>.Instance);

        var variants = await provider.ListVariantsAsync("windows11", CancellationToken.None);
        Assert.True(variants.Count >= 30, $"only {variants.Count} languages listed");
        var german = Assert.Single(variants, v => v.Language == "de-DE");
        Assert.Contains("x64", german.Architectures);

        DownloadRequest request;
        try
        {
            request = await provider.ResolveAsync(german, "x64", CancellationToken.None);
        }
        catch (MicrosoftDownloadBlockedException ex)
        {
            output.WriteLine($"Microsoft refused this network ({ex.MessageCode}); open {ex.ManualUrl} in a browser.");
            return;
        }

        Assert.Equal("software.download.prss.microsoft.com", request.Url.Host);
        Assert.EndsWith(".iso", request.Url.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.Single(request.ExpectedHashes);

        var (status, total) = await ProbeAsync(request.Url);
        Assert.Equal(HttpStatusCode.PartialContent, status);
        Assert.True(total > 3L * 1024 * 1024 * 1024, $"ISO of {total} bytes");
        output.WriteLine($"Resolved {request.Url.AbsolutePath} ({total} bytes), published SHA-256 {request.ExpectedHashes[0].Hex}");

        var fresh = await request.LinkResolver!(CancellationToken.None);
        Assert.NotEqual(request.Url, fresh);
        Assert.Equal(request.Url.AbsolutePath, fresh.AbsolutePath);
    }

    [LiveFact]
    public async Task Windows10Iso_ListsBothWordSizesAndTheEndOfSupport()
    {
        var provider = new MicrosoftIsoProvider(Http, NullLogger<MicrosoftIsoProvider>.Instance);

        var variants = await provider.ListVariantsAsync("windows10", CancellationToken.None);

        Assert.True(variants.Count >= 30);
        Assert.All(variants, v =>
        {
            Assert.Equal(["x64", "x86"], v.Architectures);
            Assert.Equal(new DateOnly(2025, 10, 14), v.EndOfSupport);
        });
    }

    [LiveFact]
    public async Task MediaCreationToolCatalog_Windows11_ListsEsdFilesThatStillExistWithTheSizeInTheCatalog()
    {
        var provider = new MediaCreationToolProvider(Http, NullLogger<MediaCreationToolProvider>.Instance);

        var variants = await provider.ListVariantsAsync("windows11-mct", CancellationToken.None);
        Assert.True(variants.Count >= 70, $"only {variants.Count} variants listed");
        output.WriteLine($"Catalog signature: {variants[0].Properties["catalogSignature"]}; version {variants[0].Version}");

        var german = Assert.Single(variants, v => v.Id == "consumer/de-de");
        var request = await provider.ResolveAsync(german, "x64", CancellationToken.None);

        var (status, total) = await ProbeAsync(request.Url);
        Assert.Equal(HttpStatusCode.PartialContent, status);
        Assert.Equal(request.ExpectedSize, total);
    }

    [LiveFact]
    public async Task MediaCreationToolCatalog_Windows10_ListsThirtyTwoBitToo()
    {
        var provider = new MediaCreationToolProvider(Http, NullLogger<MediaCreationToolProvider>.Instance);

        var variants = await provider.ListVariantsAsync("windows10-mct", CancellationToken.None);

        Assert.Contains("x86", Assert.Single(variants, v => v.Id == "consumer/de-de").Architectures);
    }

    private static async Task<(HttpStatusCode Status, long? Total)> ProbeAsync(Uri url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return (response.StatusCode, response.Content.Headers.ContentRange?.Length);
    }
}
