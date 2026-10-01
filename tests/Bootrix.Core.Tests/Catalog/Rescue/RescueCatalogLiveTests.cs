// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Bootrix.Core.Catalog.Rescue;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;

namespace Bootrix.Core.Tests.Catalog.Rescue;

/// <summary>
/// Checks the embedded catalog against the vendors' sites. Skipped unless <c>BOOTRIX_LIVE_TESTS=1</c> is set; run
/// it before publishing a catalog, and whenever one of these fails, update the data instead of the test.
/// </summary>
public class RescueCatalogLiveTests
{
    private const long SmallFileLimit = 30L * 1024 * 1024;

    private static readonly RescueCatalogDocument Catalog = EmbeddedRescueCatalog.Load();

    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(90) };

    private static IEnumerable<(RescueEntry Entry, RescueVariant Variant)> Downloads =>
        Catalog.Entries.SelectMany(e => e.Variants.Where(v => v.Sources.Count > 0).Select(v => (e, v)));

    /// <summary>Asks for the first byte only, which tells whether the file is there and, from Content-Range, how big it is.</summary>
    private static async Task<(HttpStatusCode Status, long? Length)> ProbeAsync(Uri url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var length = response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        return (response.StatusCode, length);
    }

    [LiveFact]
    public async Task EveryDirectLinkAnswersAndHasTheSizeTheCatalogStates()
    {
        var problems = new List<string>();
        var answered = 0;

        foreach (var (entry, variant) in Downloads)
        {
            foreach (var source in variant.Sources)
            {
                try
                {
                    var (status, length) = await ProbeAsync(source.Url);
                    answered++;

                    if (status is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                    {
                        problems.Add($"{entry.Id}/{variant.Id}: {source.Url} answers {(int)status}");
                    }
                    else if (variant.Size is { } size && length is { } actual && actual != size)
                    {
                        problems.Add($"{entry.Id}/{variant.Id}: {source.Url} is {actual} bytes, the catalog says {size}");
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    problems.Add($"{entry.Id}/{variant.Id}: {source.Url} is not reachable ({ex.Message})");
                }
            }
        }

        if (answered == 0)
        {
            // No network at all: nothing to check, and a missing connection is no fault of the catalog.
            return;
        }

        Assert.Empty(problems);
    }

    [LiveFact]
    public async Task VendorPagesAndManualAddressesStillExist()
    {
        var addresses = Catalog.Entries
            .SelectMany(e => e.Variants.Where(v => v.ManualUrl is not null).Select(v => (Name: $"{e.Id}/{v.Id}", Url: v.ManualUrl!))
                .Append((Name: e.Id + " homepage", Url: e.Homepage)))
            .ToList();
        var problems = new List<string>();
        var answered = 0;

        foreach (var (name, url) in addresses)
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                answered++;

                // Vendors that sit behind bot protection answer 403 to a client without a browser; only a vanished page is a finding.
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone || (int)response.StatusCode >= 500)
                {
                    problems.Add($"{name}: {url} answers {(int)response.StatusCode}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                problems.Add($"{name}: {url} is not reachable ({ex.Message})");
            }
        }

        if (answered == 0)
        {
            return;
        }

        Assert.Empty(problems);
    }

    [LiveFact]
    public async Task SmallFilesDownloadAndMatchTheDigestInTheCatalog()
    {
        using var dir = new TempDirectory();

        foreach (var (entry, variant) in Downloads.Where(d => d.Variant.Sha256 is not null && d.Variant.Size <= SmallFileLimit))
        {
            var request = new DownloadRequest([.. variant.Sources])
            {
                ExpectedSize = variant.Size,
                ExpectedHashes = [new FileHash(HashKind.Sha256, variant.Sha256!)],
            };

            // The downloader throws when size or digest differ, which is the check.
            var result = await new SegmentedDownloader().DownloadAsync(request, dir.File($"{entry.Id}-{variant.Id}.bin"));

            Assert.Equal(variant.Size, result.Length);
            Assert.Equal(variant.Sha256, result.Sha256);
        }
    }
}
