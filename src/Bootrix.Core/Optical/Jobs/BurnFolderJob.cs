// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Optical.Jobs;

public sealed record BurnFolderRequest
{
    public required IReadOnlyList<OpticalDrive> Drives { get; init; }

    public required FolderBurnRequest Folder { get; init; }

    public BurnOptions Options { get; init; } = new();
}

/// <summary>
/// Builds a data disc from a folder (ISO 9660, Joliet and UDF as chosen by <see cref="FolderBurnPlanner"/>) and burns it,
/// to several drives at once if wanted. The folder is examined before any disc is touched, so a Windows installation tree
/// with a file beyond 4 GB is turned away with the hint to use the ISO remaster instead of failing inside IMAPI.
/// </summary>
public sealed class BurnFolderJob(IOpticalService service, ILogger<BurnFolderJob> logger)
{
    public const string ScanKey = "folder.scan";

    /// <summary>The <see cref="FolderBurnPlan"/> per drive, keyed by <see cref="OpticalDrive.Id"/>.</summary>
    public const string PlansKey = "folder.plans";

    public const string ReportKey = "burn.report";

    public IJob Create(BurnFolderRequest request)
    {
        if (request.Drives.Count == 0)
        {
            throw new BootrixException(ErrorCode.NoRecorder, "no drive selected");
        }

        request.Options.Validate();
        if (request.Options.ReadBackSha256)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "read-back comparison needs an image file")
            {
                Arguments = ["The SHA-256 read-back is only available when an image is burned; use the drive's own verification for folders."],
            };
        }

        if (request.Folder.BootEntries.FirstOrDefault(e => !File.Exists(e.ImagePath)) is { } missing)
        {
            throw new BootrixException(ErrorCode.ImageUnreadable, missing.ImagePath) { Arguments = [$"boot image '{missing.ImagePath}' does not exist"] };
        }

        var run = new Run(service, logger, request);
        return new Job(
            "folder-" + Guid.NewGuid().ToString("N")[..8],
            Path.GetFileName(request.Folder.SourceFolder.TrimEnd('\\', '/')),
            [
                new DelegateJobStep("Folder.Scan", 3, run.ScanAsync),
                new DelegateJobStep("Burn.CheckMedia", 1, run.CheckMediaAsync),
                new DelegateJobStep("Burn.Write", 95, run.WriteAsync),
                new DelegateJobStep("Burn.Finish", 1, run.FinishAsync),
            ]);
    }

    private sealed class Run(IOpticalService service, ILogger logger, BurnFolderRequest request)
    {
        private readonly Dictionary<string, FolderBurnPlan> _plans = [];

        public async Task ScanAsync(JobContext context, CancellationToken cancellationToken)
        {
            var scan = await Task.Run(() => FolderBurnPlanner.Scan(request.Folder.SourceFolder, cancellationToken), cancellationToken).ConfigureAwait(false);
            context.Set(ScanKey, scan);
        }

        public async Task CheckMediaAsync(JobContext context, CancellationToken cancellationToken)
        {
            var scan = context.Get<FolderScan>(ScanKey);
            foreach (var drive in request.Drives)
            {
                var media = await BurnSupport.RequireWritableAsync(service, drive, scan.PayloadSectors, request.Options, cancellationToken).ConfigureAwait(false);
                _plans[drive.Id] = FolderBurnPlanner.Plan(scan, request.Folder, media, request.Options.VolumeLabel);
            }

            context.Set<IReadOnlyDictionary<string, FolderBurnPlan>>(PlansKey, _plans);
        }

        public async Task WriteAsync(JobContext context, CancellationToken cancellationToken)
        {
            var sectors = context.Get<FolderScan>(ScanKey).PayloadSectors;
            var results = await BurnSupport.BurnInParallelAsync(
                request.Drives,
                sectors,
                context,
                (drive, progress) =>
                {
                    var plan = _plans[drive.Id];
                    var folder = request.Folder with { FileSystems = plan.FileSystems, UdfRevision = plan.UdfRevision };
                    return service.BurnFolderAsync(drive, folder, request.Options with { VolumeLabel = plan.VolumeLabel }, progress, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);

            context.Set(ReportKey, new BurnReport(results, null));
            if (BurnSupport.FirstFailure(results) is { } failure)
            {
                throw failure;
            }

            logger.LogInformation("Folder {Folder} burned to {Count} disc(s)", request.Folder.SourceFolder, results.Count);
        }

        public async Task FinishAsync(JobContext context, CancellationToken cancellationToken)
        {
            if (!request.Options.EjectWhenDone)
            {
                return;
            }

            foreach (var drive in request.Drives)
            {
                try
                {
                    await service.EjectAsync(drive, cancellationToken).ConfigureAwait(false);
                }
                catch (BootrixException ex)
                {
                    logger.LogWarning(ex, "Could not eject {Drive}", drive.DisplayName);
                }
            }
        }
    }
}
