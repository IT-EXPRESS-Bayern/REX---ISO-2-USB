// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Diagnostics;
using Bootrix.Core.Engine;
using Bootrix.Core.Hosting;
using Bootrix.Core.Jobs;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Tiny;

namespace Bootrix.Windows.Diagnostics;

/// <summary>Packs the logs of this (elevated) process and hands the archive to the user, who could not read the folder directly.</summary>
public sealed class CollectLogsHandler(BootrixPaths paths, IUserFiles files) : EngineJobHandler<CollectLogsJobRequest>
{
    protected override async Task<EngineJobResult> RunAsync(
        CollectLogsJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var stage = Path.Combine(paths.WorkDirectory, "logs-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            Directory.CreateDirectory(paths.WorkDirectory);
            await using (var archive = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                DiagnosticsPackage.Write(
                    archive,
                    [
                        new DiagnosticsSource(paths.LogDirectory, "*.log", "broker-logs"),
                        new DiagnosticsSource(paths.JournalDirectory, "*.json", "broker-jobs", MaxFiles: 20),
                    ],
                    SystemReport.Describe(ProcessElevation.IsElevated()));
            }

            await files.CopyOutAsync(stage, request.OutputPath, (_, _) => { }, cancellationToken).ConfigureAwait(false);
            return new EngineJobResult { Outcome = JobOutcome.Succeeded, Duration = System.Diagnostics.Stopwatch.GetElapsedTime(started) };
        }
        catch (OperationCanceledException)
        {
            return new EngineJobResult { Outcome = JobOutcome.Canceled, Duration = System.Diagnostics.Stopwatch.GetElapsedTime(started) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Errors.BootrixException)
        {
            return EngineJobResult.From(new JobResult(JobOutcome.Failed, System.Diagnostics.Stopwatch.GetElapsedTime(started), ex));
        }
        finally
        {
            File.Delete(stage);
        }
    }
}
