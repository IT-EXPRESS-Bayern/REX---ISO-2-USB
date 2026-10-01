// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Storage.Testing;

namespace Bootrix.Windows.Jobs;

public sealed class StickTestHandler(IDiskService disks, JobRunner runner, StickTestJob job) : EngineJobHandler<StickTestJobRequest>
{
    protected override async Task<EngineJobResult> RunAsync(
        StickTestJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken)
    {
        var targets = new List<StickTestTarget>();
        foreach (var target in request.Targets)
        {
            var device = await Task.Run(() => disks.Find(target.DevicePath), cancellationToken).ConfigureAwait(false)
                ?? throw new Core.Errors.BootrixException(Core.Errors.ErrorCode.DeviceNotFound, target.DevicePath);
            targets.Add(new StickTestTarget(device, target.Identity));
        }

        var result = await runner.RunAsync(job.Create(targets, request.Mode), new DelegateProgressSink(progress.Report), cancellationToken, abortToken).ConfigureAwait(false);
        var summary = EngineJobResult.From(result);
        return result.Succeeded && result.Values.TryGetValue(StickTestJob.ReportKey, out var value) && value is IReadOnlyList<StickTestReport> reports
            ? summary with { ReportJson = StickTestReport.Serialize(reports) }
            : summary;
    }
}
