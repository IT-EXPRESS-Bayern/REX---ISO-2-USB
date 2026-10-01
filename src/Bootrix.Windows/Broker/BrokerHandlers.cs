// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Hosting;
using Bootrix.Core.Jobs;
using Bootrix.Core.Wim;
using Bootrix.Windows.Diagnostics;
using Bootrix.Windows.Dism;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Tiny;
using Bootrix.Windows.Tools;
using Bootrix.Windows.Workshop;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Broker;

/// <summary>What the broker has at hand when it builds the handlers for the kinds of job it runs besides the built-in ones.</summary>
internal sealed record BrokerHandlerContext(
    DiskEnumerator Disks,
    IClientImpersonator Client,
    IImageStreamProvider Images,
    JobJournal Journal,
    JobRunner Jobs,
    BootrixPaths Paths,
    ILoggerFactory Loggers);

internal static class BrokerHandlers
{
    /// <summary>One entry per kind of job. Files of the user are only ever touched through <see cref="BrokerHandlerContext.Client"/> or <see cref="BrokerHandlerContext.Images"/>.</summary>
    public static IReadOnlyList<IEngineJobHandler> Create(BrokerHandlerContext context)
    {
        var userFiles = new ClientUserFiles(context.Client);
        var installTools = new WimInstallImageTools();
        var tiny = new TinyBuildRunner(
            new Core.Tiny.TinyBuilder(new DismImageServicing(), new ImageFileSystem(), installTools, new OscdimgIsoWriter(new OscdimgLocator())),
            installTools,
            context.Jobs,
            context.Paths,
            context.Loggers.CreateLogger<TinyBuildRunner>(),
            userFiles);

        return
        [
            new CaptureCustomerPcHandler(new CustomerPcCaptureService(context.Loggers.CreateLogger<CustomerPcCaptureService>()), context.Paths),
            new CollectLogsHandler(context.Paths, userFiles),
            new StickTestHandler(context.Disks, context.Jobs, new StickTestJob(context.Disks, context.Loggers.CreateLogger<StickTestJob>())),
            new TinyBuildHandler(tiny),
        ];
    }
}
