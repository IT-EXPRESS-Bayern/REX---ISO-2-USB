// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical.Reading;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Optical.Jobs;

public sealed record RipDiscRequest
{
    public required OpticalDrive Drive { get; init; }

    public required string IsoPath { get; init; }

    public RipOptions Options { get; init; } = new();

    /// <summary>Write a .sha256 file and, if sectors were unreadable, a list of them next to the image.</summary>
    public bool WriteSidecarFiles { get; init; } = true;

    public bool EjectWhenDone { get; init; }
}

/// <summary>
/// Reads a data disc into an ISO file. A rip that is cancelled or fails halfway leaves the image and its
/// checkpoint in place, so running the same job again continues instead of starting over.
/// </summary>
public sealed class RipDiscJob(IOpticalService service, ILogger<RipDiscJob> logger)
{
    public const string ReportKey = "rip.report";

    private const string OptionsKey = "rip.options";

    public IJob Create(RipDiscRequest request)
    {
        var run = new Run(service, logger, request);
        return new Job(
            "rip-" + Guid.NewGuid().ToString("N")[..8],
            Path.GetFileName(request.IsoPath),
            [
                new DelegateJobStep("Rip.Check", 1, run.CheckAsync),
                new DelegateJobStep("Rip.Read", 95, run.ReadAsync),
                new DelegateJobStep("Rip.Finish", 4, run.FinishAsync),
            ]);
    }

    /// <summary>Names of the files written next to the image.</summary>
    public static string HashFilePath(string isoPath) => isoPath + ".sha256";

    public static string BadSectorFilePath(string isoPath) => isoPath + ".badsectors.txt";

    private sealed class Run(IOpticalService service, ILogger logger, RipDiscRequest request)
    {
        public async Task CheckAsync(JobContext context, CancellationToken cancellationToken)
        {
            var media = await service.QueryMediaAsync(request.Drive, cancellationToken).ConfigureAwait(false);
            if (!media.IsPresent)
            {
                throw new BootrixException(ErrorCode.DeviceNotFound, $"no disc in {request.Drive.DisplayName}");
            }

            // On DVD+RW, DVD-RAM and BD-RE the drive reports the formatted size, which says nothing about how much of it is used.
            var trim = request.Options.TrimToFileSystem || media.Type.HasFormattedCapacity();
            context.Set(OptionsKey, request.Options with { TrimToFileSystem = trim });
        }

        public async Task ReadAsync(JobContext context, CancellationToken cancellationToken)
        {
            var progress = new Progress<RipProgress>(p =>
                context.ReportBytes(SectorMath.ToBytes(p.SectorsDone), SectorMath.ToBytes(p.SectorsTotal), p.Phase == RipPhase.Reading ? null : p.Phase.ToString()));

            var report = await service.RipToIsoAsync(request.Drive, request.IsoPath, context.Get<RipOptions>(OptionsKey), progress, cancellationToken).ConfigureAwait(false);
            context.Set(ReportKey, report);
            logger.LogInformation("Disc read into {Path}: {Bad} unreadable sector(s)", request.IsoPath, report.BadSectors.Count);
        }

        public async Task FinishAsync(JobContext context, CancellationToken cancellationToken)
        {
            var report = context.Get<RipReport>(ReportKey);
            if (request.WriteSidecarFiles)
            {
                await File.WriteAllTextAsync(
                    HashFilePath(request.IsoPath),
                    $"{report.Sha256} *{Path.GetFileName(request.IsoPath)}\n",
                    cancellationToken).ConfigureAwait(false);

                var badFile = BadSectorFilePath(request.IsoPath);
                if (report.IsComplete)
                {
                    File.Delete(badFile);
                }
                else
                {
                    await File.WriteAllTextAsync(badFile, DescribeBadSectors(request.IsoPath, report), cancellationToken).ConfigureAwait(false);
                }
            }

            if (request.EjectWhenDone)
            {
                try
                {
                    await service.EjectAsync(request.Drive, cancellationToken).ConfigureAwait(false);
                }
                catch (BootrixException ex)
                {
                    logger.LogWarning(ex, "Could not eject {Drive}", request.Drive.DisplayName);
                }
            }
        }

        private static string DescribeBadSectors(string isoPath, RipReport report)
        {
            var text = new StringBuilder();
            text.AppendLine(CultureInfo.InvariantCulture, $"# Unreadable sectors of {Path.GetFileName(isoPath)} (2048 bytes each, counted from 0).");
            text.AppendLine("# These sectors are zero-filled in the image; the SHA-256 is that of the image as written.");
            text.AppendLine(CultureInfo.InvariantCulture, $"# {report.BadSectors.Count} of {report.SectorCount} sectors");
            foreach (var range in report.BadSectors.Ranges)
            {
                text.AppendLine(range.ToString());
            }

            return text.ToString();
        }
    }
}
