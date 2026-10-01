// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using Bootrix.Core.Engine;
using Bootrix.Core.Hosting;
using Bootrix.Core.Jobs;
using Bootrix.Windows.Diagnostics;
using Bootrix.Windows.Tiny;

namespace Bootrix.Windows.Tests.Diagnostics;

public sealed class CollectLogsHandlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootrix-collect-" + Guid.NewGuid().ToString("N")[..10]);

    public CollectLogsHandlerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [WindowsFact]
    public async Task TheLogsOfTheEngineEndUpInTheUsersArchive()
    {
        var paths = new BootrixPaths { DataDirectory = Path.Combine(_root, "data") };
        Directory.CreateDirectory(paths.LogDirectory);
        File.WriteAllText(Path.Combine(paths.LogDirectory, "broker-20261001.log"), "started");
        var output = Path.Combine(_root, "out.zip");
        IEngineJobHandler handler = new CollectLogsHandler(paths, new DirectUserFiles());

        var result = await handler.RunAsync(new CollectLogsJobRequest { OutputPath = output }, new Progress<ProgressReport>(), default, default);

        Assert.True(result.Succeeded);
        using var zip = ZipFile.OpenRead(output);
        Assert.Contains(zip.Entries, e => e.FullName == "broker-logs/broker-20261001.log");
        Assert.Contains(zip.Entries, e => e.FullName == "system-info.txt");
        Assert.Empty(Directory.GetFiles(paths.WorkDirectory));
    }

    [WindowsFact]
    public async Task AnUnwritableTargetIsReportedAsAFailedJob()
    {
        var paths = new BootrixPaths { DataDirectory = Path.Combine(_root, "data") };
        IEngineJobHandler handler = new CollectLogsHandler(paths, new DirectUserFiles());

        var result = await handler.RunAsync(new CollectLogsJobRequest { OutputPath = Path.Combine(_root, "missing", "out.zip") }, new Progress<ProgressReport>(), default, default);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
    }
}
