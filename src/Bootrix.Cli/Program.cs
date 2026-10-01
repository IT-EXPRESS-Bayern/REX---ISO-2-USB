// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Commands;
using Bootrix.Core;
using Bootrix.Core.Hosting;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Tiny;
using Bootrix.Windows;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Tiny;
using Microsoft.Extensions.DependencyInjection;

var paths = BootrixPaths.Detect(AppContext.BaseDirectory);
Directory.CreateDirectory(paths.DataDirectory);

await using var services = new ServiceCollection()
    .AddBootrixCore(paths)
    .AddBootrixWindows()
    .BuildServiceProvider();

var root = new RootCommand($"{AppInfo.Name} {AppInfo.Version} - bootable media for USB sticks, discs and rescue systems")
{
    DisksCommand.Create(services.GetRequiredService<IDiskService>()),
    HashCommand.Create(),
    WriteCommand.Create(
        services.GetRequiredService<IDiskService>(),
        services.GetRequiredService<RawWriteJob>(),
        services.GetRequiredService<JobRunner>()),
    TinyCommand.Create(
        services.GetRequiredService<TinyBuildRunner>(),
        services.GetRequiredService<IInstallImageTools>(),
        paths.WorkDirectory),
};

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
