// SPDX-License-Identifier: GPL-3.0-or-later
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
/// detaches the ISO again. The CLI calls it directly, the broker calls it for the GUI.
/// </summary>
public sealed class TinyBuildRunner(TinyBuilder builder, IInstallImageTools tools, JobRunner runner, BootrixPaths paths, ILogger<TinyBuildRunner> logger)
{
    public async Task<JobResult> RunAsync(
        TinyBuildJobRequest request,
        IProgressSink? progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken = default)
    {
        try
        {
            if (!File.Exists(request.IsoPath))
            {
                throw new BootrixException(ErrorCode.ImageUnreadable, request.IsoPath) { Arguments = [request.IsoPath] };
            }

            using var mounted = VirtualDiskMounter.MountIso(request.IsoPath, cancellationToken);
            var root = mounted.RootPath ?? throw new BootrixException(ErrorCode.ImageMountFailed, request.IsoPath);

            var editions = await tools.GetEditionsAsync(FindInstallImage(root), cancellationToken).ConfigureAwait(false);
            var profile = TinyProfiles.Load(request.ProfileId.ToLowerInvariant());

            var options = new TinyBuildOptions
            {
                SourceRoot = root,
                WorkDirectory = Path.Combine(request.WorkDirectory ?? paths.WorkDirectory, "tiny-" + Guid.NewGuid().ToString("N")[..8]),
                ImageIndex = EditionPicker.Pick(editions, request.Edition),
                ProfileId = profile.Id,
                DisabledGroups = TinyProfiles.DisabledGroups(profile, request.KeepGroups, request.IncludeGroups),
                IsoPath = request.OutputIsoPath,
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
