// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Bootrix.Core.Tests.Net.Support;
using Xunit.Abstractions;

namespace Bootrix.Core.Tests.Catalog.Distros;

/// <summary>
/// The thorough counterpart of <see cref="LiveDistroTests"/> (<c>BOOTRIX_LIVE_TESTS=1</c>): every product, every
/// variant and every architecture of a provider is resolved against the vendor's real servers, signature checks
/// included, and the first address of each is asked for its size. It finds file-name patterns that only hold for some
/// variants, such as a spin whose checksum file is named differently.
/// </summary>
public class LiveDistroCatalogTests(ITestOutputHelper output)
{
    private static HttpClient Http => LiveVendor.Http;

    [LiveFact]
    public Task Ubuntu() => Everything(new UbuntuProvider(Http));

    [LiveFact]
    public Task Debian() => Everything(new DebianProvider(Http));

    [LiveFact]
    public Task LinuxMint() => Everything(new LinuxMintProvider(Http));

    [LiveFact]
    public Task Fedora() => Everything(new FedoraProvider(Http));

    [LiveFact]
    public Task ArchLinux() => Everything(new ArchLinuxProvider(Http));

    [LiveFact]
    public Task OpenSuse() => Everything(new OpenSuseProvider(Http));

    [LiveFact]
    public Task Kali() => Everything(new KaliProvider(Http));

    [LiveFact]
    public Task Manjaro() => Everything(new ManjaroProvider(Http));

    [LiveFact]
    public Task PopOs() => Everything(new PopOsProvider(Http));

    [LiveFact]
    public Task ElementaryOs() => Everything(new ElementaryOsProvider(Http));

    [LiveFact]
    public Task ZorinOs() => Everything(new ZorinOsProvider(Http));

    [LiveFact]
    public Task TrueNas() => Everything(new TrueNasProvider(Http));

    [LiveFact]
    public Task Proxmox() => Everything(new ProxmoxProvider(Http));

    [LiveFact]
    public Task Clonezilla() => Everything(new ClonezillaProvider(Http));

    [LiveFact]
    public Task GPartedLive() => Everything(new GPartedLiveProvider(Http));

    [LiveFact]
    public Task Rescuezilla() => Everything(new RescuezillaProvider(Http));

    [LiveFact]
    public Task Memtest86Plus() => Everything(new Memtest86PlusProvider(Http));

    [LiveFact]
    public Task FreeBsd() => Everything(new FreeBsdProvider(Http));

    private async Task Everything(ICatalogProvider provider)
    {
        var failures = new List<string>();
        var checkedVariants = 0;

        try
        {
            foreach (var product in await provider.ListProductsAsync(CancellationToken.None))
            {
                var variants = await provider.ListVariantsAsync(product.Id, CancellationToken.None);
                foreach (var variant in variants)
                {
                    checkedVariants += await CheckVariant(provider, variant, failures);
                }

                output.WriteLine($"{provider.Id}/{product.Id}: {variants.Count} variant(s)");
            }
        }
        catch (BootrixException ex) when (LiveVendor.IsOffline(ex))
        {
            return;
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        output.WriteLine($"{provider.Id}: {checkedVariants} download(s) resolved and probed");
    }

    private static async Task<int> CheckVariant(ICatalogProvider provider, CatalogVariant variant, List<string> failures)
    {
        if (variant.ManualUrl is not null)
        {
            if (!variant.ManualUrl.StartsWith("https://", StringComparison.Ordinal))
            {
                failures.Add($"{variant.Id}: manual address {variant.ManualUrl} is not https");
            }

            return 0;
        }

        var resolved = 0;
        foreach (var arch in variant.Architectures.Count > 0 ? variant.Architectures.Select(a => (string?)a) : [null])
        {
            try
            {
                var request = await provider.ResolveAsync(variant, arch, CancellationToken.None);
                if (request.ExpectedHashes.Count == 0)
                {
                    failures.Add($"{variant.Id} [{arch}]: no digest");
                    continue;
                }

                var length = await LiveVendor.ProbeLength(request.Url);
                if (length < 100_000 || (request.ExpectedSize is { } expected && expected != length))
                {
                    failures.Add($"{variant.Id} [{arch}]: {request.Url} has {length} bytes, expected {request.ExpectedSize}");
                    continue;
                }

                resolved++;
            }
            catch (Exception ex) when (ex is BootrixException or HttpRequestException or Xunit.Sdk.XunitException)
            {
                failures.Add($"{variant.Id} [{arch}]: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return resolved;
    }
}
