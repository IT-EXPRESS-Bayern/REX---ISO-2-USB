// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Commands;
using Bootrix.Cli.Output;
using Bootrix.Core;
using Bootrix.Core.Hosting;
using Bootrix.Core.Catalog;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Writing;
using Bootrix.Core.Library;
using Bootrix.Core.Net;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Tiny;
using Bootrix.Windows;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Tiny;
using Microsoft.Extensions.DependencyInjection;

var paths = BootrixPaths.Detect(AppContext.BaseDirectory);
try
{
    // The CLI runs elevated and writes its log, journal and work files here, so standard users must not be able to change the folder.
    ProtectedFolder.Ensure(paths.DataDirectory);
}
catch (BootrixException ex)
{
    new ConsoleWriter(json: false).WriteError(ex);
    return ExitCodes.For(ex);
}

await using var services = new ServiceCollection()
    .AddBootrixCore(paths)
    .AddBootrixCatalog(paths)
    .AddBootrixWindows()
    .BuildServiceProvider();

var root = new RootCommand($"{AppInfo.Name} {AppInfo.Version} - bootable media for USB sticks, discs and rescue systems")
{
    DisksCommand.Create(Lazily<IDiskService>()),
    HashCommand.Create(),
    WriteCommand.Create(Lazily<IEngine>(), Lazily<IDiskService>()),
    WriteCommand.CreateFormat(Lazily<IEngine>(), Lazily<IDiskService>()),
    WriteCommand.CreateDos(Lazily<IEngine>(), Lazily<IDiskService>()),
    ToolsCommands.CreateVerify(Lazily<IEngine>(), Lazily<IDiskService>()),
    ToolsCommands.CreateRestore(Lazily<IEngine>(), Lazily<IDiskService>()),
    ToolsCommands.CreateTest(Lazily<IEngine>(), Lazily<IDiskService>()),
    PlanCommand.Create(services.GetRequiredService<MediaPlanService>(), Lazily<IDiskService>()),
    InspectCommand.Create(services.GetRequiredService<ImageInspector>()),
    CatalogCommand.Create(services.GetRequiredService<CatalogService>()),
    DownloadCommand.Create(
        services.GetRequiredService<CatalogService>(),
        services.GetRequiredService<SegmentedDownloader>(),
        services.GetRequiredService<ImageLibrary>()),
    LibraryCommand.Create(services.GetRequiredService<ImageLibrary>()),
    DiscCommand.Create(
        Lazily<IOpticalService>(),
        Lazily<BurnImageJob>(),
        Lazily<RipDiscJob>(),
        Lazily<EraseDiscJob>(),
        services.GetRequiredService<JobRunner>()),
    TinyCommand.Create(Lazily<TinyBuildRunner>(), Lazily<IInstallImageTools>(), paths.WorkDirectory),
};

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

// Windows services are created when a command needs them, so commands that only look at files (hash, inspect, plan)
// start without touching the platform layer.
Lazy<T> Lazily<T>() where T : notnull => new(() => services.GetRequiredService<T>());
