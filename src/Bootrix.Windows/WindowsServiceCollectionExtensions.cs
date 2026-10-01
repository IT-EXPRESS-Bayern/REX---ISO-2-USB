// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Storage;
using Bootrix.Core.Tiny;
using Bootrix.Core.Wim;
using Bootrix.Windows.Broker;
using Bootrix.Windows.Dism;
using Bootrix.Windows.Engine;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Bootrix.Windows;

public static class WindowsServiceCollectionExtensions
{
    public static IServiceCollection AddBootrixWindows(this IServiceCollection services)
    {
        services.AddSingleton<IDiskService, DiskEnumerator>();
        services.AddSingleton<IImageStreamProvider, FileImageStreamProvider>();
        services.AddSingleton<RawWriteJob>();
        services.AddSingleton<DiskPreparer>();

        services.AddSingleton<IImageServicing, DismImageServicing>();
        services.AddSingleton<IImageFileSystem, ImageFileSystem>();
        services.AddSingleton<IInstallImageTools, WimInstallImageTools>();
        services.AddSingleton<OscdimgLocator>();
        services.AddSingleton<IIsoWriter, OscdimgIsoWriter>();
        services.AddSingleton<TinyBuilder>();

        services.AddSingleton<LocalEngine>();
        services.AddSingleton<BrokerLauncher>();
        services.AddSingleton<EngineProvider>();
        services.AddSingleton(sp => sp.GetRequiredService<EngineProvider>().Engine);

        return services;
    }
}
