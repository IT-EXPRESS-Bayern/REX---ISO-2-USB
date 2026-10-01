// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Tiny;
using Bootrix.Core.Wim;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Capture;
using Bootrix.Windows.Dism;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Optical;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Tiny;
using Bootrix.Windows.Workshop;
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
        services.AddSingleton<TinyBuildRunner>();

        services.AddSingleton<IOpticalService, ImapiOpticalService>();
        services.AddSingleton<BurnImageJob>();
        services.AddSingleton<BurnFolderJob>();
        services.AddSingleton<RipDiscJob>();
        services.AddSingleton<EraseDiscJob>();

        services.AddSingleton<ITargetPcCollector, TargetPcCollector>();
        services.AddSingleton<ICustomerPcCapture, CustomerPcCaptureService>();

        return services;
    }
}
