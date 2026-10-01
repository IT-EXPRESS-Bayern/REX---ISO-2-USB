// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using Bootrix.Core.Engine;
using Bootrix.Core.Hosting;
using Bootrix.Core.Jobs;
using Bootrix.Core.Json;
using Bootrix.Core.Workshop.Advice;
using Bootrix.Core.Workshop.Capture;

namespace Bootrix.Windows.Workshop;

/// <summary>Runs the capture of a customer PC for the window, which cannot read Wi-Fi keys or recovery passwords itself.</summary>
public sealed class CaptureCustomerPcHandler(ICustomerPcCapture capture, BootrixPaths paths) : EngineJobHandler<CaptureCustomerPcJobRequest>
{
    protected override async Task<EngineJobResult> RunAsync(
        CaptureCustomerPcJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken)
    {
        var started = Stopwatch.GetTimestamp();

        // The exported profiles hold the keys in clear text, so they live in the protected work folder and only as long as it takes to read them.
        string? exportDirectory = request.IncludeWlanKeys ? Path.Combine(paths.WorkDirectory, "wlan-" + Guid.NewGuid().ToString("N")[..8]) : null;
        try
        {
            if (exportDirectory is not null)
            {
                Directory.CreateDirectory(exportDirectory);
            }

            var result = await capture.CaptureAsync(
                new CustomerPcCaptureOptions
                {
                    IncludeInstalledPrograms = request.IncludeInstalledPrograms,
                    IncludeThirdPartyDrivers = request.IncludeThirdPartyDrivers,
                    ListWlanProfiles = request.ListWlanProfiles,
                    WlanExportDirectory = exportDirectory,
                    IncludeBitLockerRecoveryKeys = request.IncludeBitLockerRecoveryKeys,
                    IncludeWindowsProductKey = request.IncludeWindowsProductKey,
                },
                cancellationToken).ConfigureAwait(false);

            return new EngineJobResult
            {
                Outcome = JobOutcome.Succeeded,
                Duration = Stopwatch.GetElapsedTime(started),
                ReportJson = JsonSerializer.Serialize(Scrub(result), CoreJson.Options),
            };
        }
        catch (OperationCanceledException)
        {
            return new EngineJobResult { Outcome = JobOutcome.Canceled, Duration = Stopwatch.GetElapsedTime(started) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Errors.BootrixException)
        {
            return EngineJobResult.From(new JobResult(JobOutcome.Failed, Stopwatch.GetElapsedTime(started), ex));
        }
        finally
        {
            if (exportDirectory is not null)
            {
                Wipe(exportDirectory);
            }
        }
    }

    /// <summary>The path of an export folder that is about to be deleted means nothing outside this process.</summary>
    private static CustomerPcCapture Scrub(CustomerPcCapture capture) => capture with
    {
        WlanProfiles = capture.WlanProfiles?.Select(p => p with { ExportedFile = null }).ToList(),
        Notes = [.. capture.Notes.Where(n => n.Key != AdvisorKeys.CaptureWlanExportFiles)],
    };

    private static void Wipe(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var length = new FileInfo(file).Length;
                File.WriteAllBytes(file, new byte[length]);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind in the protected folder; the next start of the broker does not touch it, but nobody else can read it.
        }
    }
}
