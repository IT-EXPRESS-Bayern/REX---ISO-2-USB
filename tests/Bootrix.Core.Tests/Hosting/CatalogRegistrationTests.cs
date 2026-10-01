// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Hosting;
using Bootrix.Core.Library;
using Bootrix.Core.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Bootrix.Core.Tests.Hosting;

public sealed class CatalogRegistrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-di-" + Guid.NewGuid().ToString("N"));

    private ServiceProvider Build() => new ServiceCollection()
        .AddBootrixCore(BootrixPaths.ForPortable(_directory))
        .AddBootrixCatalog(BootrixPaths.ForPortable(_directory))
        .BuildServiceProvider(validateScopes: true);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void EveryRegisteredServiceCanBeResolved()
    {
        using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<CatalogService>());
        Assert.NotNull(provider.GetRequiredService<SegmentedDownloader>());
        Assert.NotNull(provider.GetRequiredService<ImageLibrary>());
        Assert.NotNull(provider.GetRequiredService<ILibraryUpdateCheck>());
    }

    [Fact]
    public void ProviderIdsAreUniqueAndCoverTheVendors()
    {
        using var provider = Build();

        var ids = provider.GetServices<ICatalogProvider>().Select(p => p.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("microsoft", ids);
        Assert.Contains("microsoft-mct", ids);
        Assert.Contains("ubuntu", ids);
        Assert.Contains("rescue", ids);
        Assert.True(ids.Count >= 21, $"only {ids.Count} providers registered");
    }

    [Fact]
    public async Task TheRescueCatalogWorksWithoutAnyNetwork()
    {
        using var provider = Build();

        var rescue = provider.GetServices<ICatalogProvider>().Single(p => p.Id == "rescue");
        var products = await rescue.ListProductsAsync(CancellationToken.None);

        Assert.NotEmpty(products);
    }

    [Fact]
    public void WithoutTrustedKeysNoCatalogUpdateIsAccepted()
    {
        using var provider = Build();

        Assert.Empty(ManifestTrust.TrustedKeys);
        var ex = Assert.Throws<Bootrix.Core.Errors.BootrixException>(() => provider.GetRequiredService<Bootrix.Core.Catalog.Rescue.RescueCatalogStore>().Apply("{}"u8));
        Assert.Equal(Bootrix.Core.Errors.ErrorCode.SignatureInvalid, ex.Code);
    }
}
