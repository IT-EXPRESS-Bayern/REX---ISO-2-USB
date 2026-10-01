// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Catalog.Rescue;
using Bootrix.Core.Library;
using Bootrix.Core.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Hosting;

public static class CatalogServiceCollectionExtensions
{
    /// <summary>
    /// Everything that finds, downloads and keeps images: the catalog with all its providers, the segmented
    /// downloader and the image library. The providers are unprivileged code and belong to the window and the CLI,
    /// never to the elevated broker.
    /// </summary>
    public static IServiceCollection AddBootrixCatalog(this IServiceCollection services, BootrixPaths paths)
    {
        var catalogCache = Path.Combine(paths.CacheDirectory, "catalog");

        services.AddSingleton(_ => CatalogHttp.Create());
        services.AddSingleton<IVersionStore>(_ => new FileVersionStore(Path.Combine(catalogCache, "versions.json")));
        services.AddSingleton(provider => new RescueCatalogStore(
            Path.Combine(catalogCache, "rescue"),
            ManifestTrust.TrustedKeys.Count == 0 ? null : new SignedManifestVerifier(ManifestTrust.TrustedKeys, provider.GetRequiredService<IVersionStore>()),
            provider.GetRequiredService<ILogger<RescueCatalogStore>>()));

        // Microsoft's handshake needs cookies; every other provider shares the stateless client.
        services.AddSingleton<ICatalogProvider>(provider => new MicrosoftIsoProvider(
            CatalogHttp.CreateWithCookies(),
            provider.GetRequiredService<ILogger<MicrosoftIsoProvider>>()));
        services.AddSingleton<ICatalogProvider, MediaCreationToolProvider>();

        services.AddSingleton<ICatalogProvider, UbuntuProvider>();
        services.AddSingleton<ICatalogProvider, DebianProvider>();
        services.AddSingleton<ICatalogProvider, LinuxMintProvider>();
        services.AddSingleton<ICatalogProvider, FedoraProvider>();
        services.AddSingleton<ICatalogProvider, ArchLinuxProvider>();
        services.AddSingleton<ICatalogProvider, OpenSuseProvider>();
        services.AddSingleton<ICatalogProvider, KaliProvider>();
        services.AddSingleton<ICatalogProvider, ManjaroProvider>();
        services.AddSingleton<ICatalogProvider, PopOsProvider>();
        services.AddSingleton<ICatalogProvider, ElementaryOsProvider>();
        services.AddSingleton<ICatalogProvider, ZorinOsProvider>();
        services.AddSingleton<ICatalogProvider, TrueNasProvider>();
        services.AddSingleton<ICatalogProvider, ProxmoxProvider>();
        services.AddSingleton<ICatalogProvider, ClonezillaProvider>();
        services.AddSingleton<ICatalogProvider, GPartedLiveProvider>();
        services.AddSingleton<ICatalogProvider, RescuezillaProvider>();
        services.AddSingleton<ICatalogProvider, Memtest86PlusProvider>();
        services.AddSingleton<ICatalogProvider, FreeBsdProvider>();
        services.AddSingleton<ICatalogProvider, RescueCatalogProvider>();

        // The rescue catalog also lists tools that the distribution providers resolve live; show them once.
        services.AddSingleton(provider => new CatalogService(
            provider.GetServices<ICatalogProvider>(),
            provider.GetRequiredService<ILogger<CatalogService>>(),
            new HashSet<string> { "rescue-clonezilla", "rescue-gparted-live", "rescue-rescuezilla", "rescue-memtest86plus" }));
        services.AddSingleton<SegmentedDownloader>();

        services.AddSingleton(provider => new ImageLibrary(
            new ImageLibraryOptions { LocalDirectory = Path.Combine(paths.CacheDirectory, "library") },
            provider.GetRequiredService<ILogger<ImageLibrary>>()));
        services.AddSingleton<ILibraryUpdateCheck, LibraryUpdateCheck>();

        return services;
    }
}
