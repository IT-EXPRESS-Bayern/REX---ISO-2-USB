// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Hosting;
using Bootrix.Core.Jobs;
using Bootrix.Core.Tiny;
using Bootrix.Windows.Images;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Tiny;

/// <summary>
/// Runs a <see cref="TinyBuildJobRequest"/>: attaches the ISO, picks the edition, lets the builder do its work and
/// detaches the ISO again. The CLI calls it directly; the broker calls it for the GUI and passes <paramref name="userFiles"/>,
/// so that the source ISO is read and the result is written as the user and never with the elevated rights.
/// </summary>
public sealed class TinyBuildRunner(
    TinyBuilder builder,
    IInstallImageTools tools,
    JobRunner runner,
    BootrixPaths paths,
    ILogger<TinyBuildRunner> logger,
    IUserFiles? userFiles = null)
{
    // Shares of the progress bar for copying the source in and the result out; the build gets what is left.
    private const double FetchShare = 0.08;
    private const double DeliverShare = 0.06;

    public async Task<JobResult> RunAsync(
        TinyBuildJobRequest request,
        IProgressSink? progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken = default)
    {
        if (userFiles is null)
        {
            return await BuildAsync(request, request.IsoPath, request.OutputIsoPath, progress, cancellationToken, abortToken).ConfigureAwait(false);
        }

        return await BuildForUserAsync(userFiles, request, progress ?? NullProgressSink.Instance, cancellationToken, abortToken).ConfigureAwait(false);
    }

    private async Task<JobResult> BuildForUserAsync(
        IUserFiles files,
        TinyBuildJobRequest request,
        IProgressSink progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken)
    {
        var started = Stopwatch.GetTimestamp();
        var stage = Path.Combine(paths.WorkDirectory, "tiny-stage-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(stage);
            var source = Path.Combine(stage, "source.iso");
            var output = Path.Combine(stage, "output.iso");

            await files.CopyInAsync(request.IsoPath, source, (done, total) => ReportTransfer(progress, "Tiny.FetchSource", 0, FetchShare, done, total), cancellationToken).ConfigureAwait(false);

            var scaled = new ScaledProgressSink(progress, FetchShare, 1 - DeliverShare);
            var result = await BuildAsync(request, source, output, scaled, cancellationToken, abortToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return result;
            }

            await files.CopyOutAsync(output, request.OutputIsoPath, (done, total) => ReportTransfer(progress, "Tiny.DeliverImage", 1 - DeliverShare, 1, done, total), cancellationToken).ConfigureAwait(false);
            return result with { Duration = Stopwatch.GetElapsedTime(started) };
        }
        catch (OperationCanceledException ex)
        {
            return new JobResult(JobOutcome.Canceled, Stopwatch.GetElapsedTime(started), ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tiny build could not move its files");
            return new JobResult(JobOutcome.Failed, Stopwatch.GetElapsedTime(started), ex);
        }
        finally
        {
            DeleteStage(stage);
        }
    }

    private async Task<JobResult> BuildAsync(
        TinyBuildJobRequest request,
        string isoPath,
        string outputIsoPath,
        IProgressSink? progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken)
    {
        try
        {
            if (!File.Exists(isoPath))
            {
                throw new BootrixException(ErrorCode.ImageUnreadable, isoPath) { Arguments = [isoPath] };
            }

            using var mounted = VirtualDiskMounter.MountIso(isoPath, cancellationToken);
            var root = mounted.RootPath ?? throw new BootrixException(ErrorCode.ImageMountFailed, isoPath);

            var editions = await tools.GetEditionsAsync(FindInstallImage(root), cancellationToken).ConfigureAwait(false);
            var profile = TinyProfiles.Load(request.ProfileId.ToLowerInvariant());

            var options = new TinyBuildOptions
            {
                SourceRoot = root,
                WorkDirectory = Path.Combine(request.WorkDirectory ?? paths.WorkDirectory, "tiny-" + Guid.NewGuid().ToString("N")[..8]),
                ImageIndex = EditionPicker.Pick(editions, request.Edition),
                ProfileId = profile.Id,
                DisabledGroups = TinyProfiles.DisabledGroups(profile, request.KeepGroups, request.IncludeGroups),
                IsoPath = outputIsoPath,
                VolumeLabel = request.VolumeLabel,
                Compression = request.Compression,
                BypassHardwareChecks = request.BypassHardwareChecks,
                Unattend = request.Unattend,
                AcknowledgeNoServicing = request.AcknowledgeNoServicing,
                KeepWorkDirectory = request.KeepWorkDirectory,
            };

            return await runner.RunAsync(builder.CreateJob(options), progress, cancellationToken, abortToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            return new JobResult(JobOutcome.Canceled, TimeSpan.Zero, ex);
        }
        catch (Exception ex)
        {
            // Problems before the builder starts (missing ISO, no edition chosen) are reported like any failed job.
            logger.LogError(ex, "Tiny build could not start");
            return new JobResult(JobOutcome.Failed, TimeSpan.Zero, ex);
        }
    }

    private static void ReportTransfer(IProgressSink progress, string key, double from, double to, long done, long total)
    {
        var fraction = total > 0 ? (double)done / total : 0;
        progress.Report(new ProgressReport("tiny-transfer", 0, 1, key, fraction, from + (to - from) * fraction, done, total, 0, null, null));
    }

    private void DeleteStage(string stage)
    {
        try
        {
            Directory.Delete(stage, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The staging folder {Folder} could not be removed", stage);
        }
    }

    /// <summary>Retail media carry install.wim; media from the Media Creation Tool carry install.esd instead.</summary>
    public static string FindInstallImage(string root)
    {
        foreach (var name in new[] { "install.wim", "install.esd" })
        {
            var candidate = Path.Combine(root, "sources", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new BootrixException(ErrorCode.ImageUnsupported, root) { Arguments = [root] };
    }
}
