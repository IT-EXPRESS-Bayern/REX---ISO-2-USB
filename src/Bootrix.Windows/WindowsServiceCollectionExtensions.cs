// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Hosting;
using Bootrix.Core.Images;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Tiny;
using Bootrix.Core.Wim;
using Bootrix.Core.Writing;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Capture;
using Bootrix.Windows.Broker;
using Bootrix.Windows.Dism;
using Bootrix.Windows.Engine;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Optical;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Diagnostics;
using Bootrix.Windows.Tiny;
using Bootrix.Windows.Tools;
using Bootrix.Windows.Workshop;
using Bootrix.Windows.Writing;
using Microsoft.Extensions.DependencyInjection;

namespace Bootrix.Windows;

public static class WindowsServiceCollectionExtensions
{
    public static IServiceCollection AddBootrixWindows(this IServiceCollection services)
    {
        services.AddSingleton<IDiskService, DiskEnumerator>();
        // Whoever runs this process reads the image with the rights they have, so sparse bundles are fine; the elevated broker builds its own provider without them.
        services.AddSingleton<IImageStreamProvider>(_ => new FileImageStreamProvider(allowSparseBundles: true));
        services.AddSingleton<RawWriteJob>();
        services.AddSingleton<RestoreDriveJob>();
        services.AddSingleton<VerifyMediaJob>();
        services.AddSingleton<StickTestJob>();
        services.AddSingleton<IEngineJobHandler, StickTestHandler>();
        services.AddSingleton<DiskPreparer>();

        services.AddSingleton<IImageServicing, DismImageServicing>();
        services.AddSingleton<IImageFileSystem, ImageFileSystem>();
        services.AddSingleton<IInstallImageTools, WimInstallImageTools>();
        services.AddSingleton<OscdimgLocator>();
        services.AddSingleton<IIsoWriter, OscdimgIsoWriter>();
        services.AddSingleton<TinyBuilder>();
        services.AddSingleton<TinyBuildRunner>();
        services.AddSingleton<IEngineJobHandler, TinyBuildHandler>();
        services.AddSingleton<IEngineJobHandler>(sp => new CollectLogsHandler(sp.GetRequiredService<BootrixPaths>(), new DirectUserFiles()));

        services.AddSingleton<IOpticalService, ImapiOpticalService>();
        services.AddSingleton<BurnImageJob>();
        services.AddSingleton<BurnFolderJob>();
        services.AddSingleton<RipDiscJob>();
        services.AddSingleton<EraseDiscJob>();

        services.AddSingleton<ITargetPcCollector, TargetPcCollector>();
        services.AddSingleton<ICustomerPcCapture, CustomerPcCaptureService>();
        services.AddSingleton<IEngineJobHandler, CaptureCustomerPcHandler>();

        services.AddSingleton<ImageInspector>();
        services.AddSingleton<MediaPlanService>();
        services.AddSingleton<WriteServices>();
        services.AddSingleton<IEnumerable<IMediaWriter>>(sp => MediaWriters.CreateDefault(
            sp.GetRequiredService<WriteServices>(),
            sp.GetRequiredService<IImageStreamProvider>(),
            sp.GetRequiredService<RawWriteJob>()));
        services.AddSingleton<WriteImageJobFactory>();

        services.AddSingleton<LocalEngine>();
        services.AddSingleton<BrokerLauncher>();
        services.AddSingleton<EngineProvider>();
        services.AddSingleton(sp => sp.GetRequiredService<EngineProvider>().Engine);

        return services;
    }
}
