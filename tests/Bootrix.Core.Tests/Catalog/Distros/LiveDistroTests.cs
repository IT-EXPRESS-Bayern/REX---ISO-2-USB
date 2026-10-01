// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using Xunit.Abstractions;

namespace Bootrix.Core.Tests.Catalog.Distros;

/// <summary>
/// Runs each provider against the vendor's real servers (<c>BOOTRIX_LIVE_TESTS=1</c>): lists the variants, resolves
/// the recommended one including the real signature check, and asks the file server for the image's size. These tests
/// are what notices a vendor changing its layout or rotating a key; the unit tests above cannot.
/// </summary>
public class LiveDistroTests(ITestOutputHelper output)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    [LiveFact]
    public Task Ubuntu() => Check(new UbuntuProvider(Http), "ubuntu", expectSigned: true);

    [LiveFact]
    public Task Kubuntu() => Check(new UbuntuProvider(Http), "kubuntu", expectSigned: true);

    [LiveFact]
    public Task LinuxMint() => Check(new LinuxMintProvider(Http), "linuxmint", expectSigned: true);

    [LiveFact]
    public Task FedoraWorkstation() => Check(new FedoraProvider(Http), "fedora-workstation", expectSigned: true);

    [LiveFact]
    public Task FedoraServer() => Check(new FedoraProvider(Http), "fedora-server", expectSigned: true, architecture: "x64");

    [LiveFact]
    public Task ArchLinux() => Check(new ArchLinuxProvider(Http), "archlinux", expectSigned: false);

    [LiveFact]
    public Task OpenSuseLeap() => Check(new OpenSuseProvider(Http), "opensuse-leap", expectSigned: true, architecture: "x64");

    [LiveFact]
    public Task OpenSuseTumbleweed() => Check(new OpenSuseProvider(Http), "opensuse-tumbleweed", expectSigned: true);

    [LiveFact]
    public Task Kali() => Check(new KaliProvider(Http), "kali", expectSigned: true);

    [LiveFact]
    public Task Debian() => Check(new DebianProvider(Http), "debian", expectSigned: true);

    /// <summary>
    /// Lists the product, resolves the recommended (or else first) variant and checks that the primary address serves
    /// a file of the announced size. A missing network ends the test quietly; any answer from a vendor does not.
    /// </summary>
    private async Task Check(ICatalogProvider provider, string productId, bool expectSigned, string? architecture = null)
    {
        try
        {
            var products = await provider.ListProductsAsync(CancellationToken.None);
            Assert.Contains(products, p => p.Id == productId);

            var variants = await provider.ListVariantsAsync(productId, CancellationToken.None);
            Assert.NotEmpty(variants);
            var variant = variants.FirstOrDefault(v => v.IsRecommended) ?? variants[0];
            Assert.All(variants, v => Assert.Equal(
                expectSigned ? DistroProperties.PinnedKey : DistroProperties.TlsOnly,
                v.Properties[DistroProperties.HashTrust]));

            if (variant.ManualUrl is not null)
            {
                Assert.StartsWith("https://", variant.ManualUrl, StringComparison.Ordinal);
                return;
            }

            var arch = architecture ?? (variant.Architectures.Count > 0 ? variant.Architectures[0] : null);
            var request = await provider.ResolveAsync(variant, arch, CancellationToken.None);

            Assert.NotEmpty(request.ExpectedHashes);
            Assert.All(request.Sources, s => Assert.StartsWith("http", s.Url.Scheme, StringComparison.Ordinal));
            var length = await ProbeLength(request.Url);
            Assert.True(length > 1_000_000, $"{request.Url} announced only {length} bytes.");
            if (request.ExpectedSize is { } expected)
            {
                Assert.Equal(expected, length);
            }

            output.WriteLine($"{provider.Id}/{variant.Id} [{arch}]: {request.Url} {length} bytes, {request.ExpectedHashes[0]}, {request.Sources.Count} source(s), {variants.Count} variant(s)");
        }
        catch (BootrixException ex) when (ex.Code == ErrorCode.CatalogUnavailable && ex.InnerException is HttpRequestException { StatusCode: null })
        {
            // No route to the vendor from here; nothing was learned about the vendor.
        }
    }

    private static async Task<long> ProbeLength(Uri url)
    {
        // A one-byte range request works where HEAD is refused, and Content-Range carries the full size.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.StatusCode is HttpStatusCode.PartialContent or HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}");

        return response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? 0;
    }
}
