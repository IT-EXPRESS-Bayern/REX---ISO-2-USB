// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical.Images;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Optical.Jobs;

public sealed record BurnImageRequest
{
    /// <summary>One or more recorders; with several, all of them burn the same image at the same time.</summary>
    public required IReadOnlyList<OpticalDrive> Drives { get; init; }

    public required DiscImageSource Image { get; init; }

    public BurnOptions Options { get; init; } = new();
}

/// <summary>
/// Burns an image to one or several discs. The disc is checked first, so that a wrong or non-blank disc is
/// reported before a long write starts, and afterwards it can be read back through the operating system and
/// compared with the source by SHA-256, independent of the drive's own verification.
/// </summary>
public sealed class BurnImageJob(IOpticalService service, ILogger<BurnImageJob> logger)
{
    public const string ReportKey = "burn.report";

    private const string DigestKey = "burn.digest";

    /// <summary>Pause between attempts to open the drive after the burn; the new disc takes a moment to be mounted.</summary>
    public TimeSpan ReadBackRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public int ReadBackAttempts { get; init; } = 30;

    public IJob Create(BurnImageRequest request)
    {
        if (request.Drives.Count == 0)
        {
            throw new BootrixException(ErrorCode.NoRecorder, "no drive selected");
        }

        request.Options.Validate();
        var run = new Run(service, logger, request, ReadBackRetryDelay, ReadBackAttempts);
        var readBack = request.Options.ReadBackSha256;

        var steps = new List<IJobStep> { new DelegateJobStep("Burn.CheckMedia", 1, run.CheckMediaAsync) };
        if (readBack)
        {
            steps.Add(new DelegateJobStep("Burn.HashSource", 10, run.HashSourceAsync));
        }

        steps.Add(new DelegateJobStep("Burn.Write", readBack ? 60 : 98, run.WriteAsync));
        if (readBack)
        {
            steps.Add(new DelegateJobStep("Burn.ReadBack", 28, run.ReadBackAsync));
        }

        steps.Add(new DelegateJobStep("Burn.Finish", 1, run.FinishAsync));
        return new Job("burn-" + Guid.NewGuid().ToString("N")[..8], request.Image.DisplayName, steps);
    }

    private sealed class Run(IOpticalService service, ILogger logger, BurnImageRequest request, TimeSpan readBackDelay, int readBackAttempts)
    {
        private IReadOnlyList<DriveBurnResult> _results = [];

        public async Task CheckMediaAsync(JobContext context, CancellationToken cancellationToken)
        {
            // Checked one after the other: a drive that is opening its tray should not hold up the message about another one.
            foreach (var drive in request.Drives)
            {
                await BurnSupport.RequireWritableAsync(service, drive, request.Image.SectorCount, request.Options, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task HashSourceAsync(JobContext context, CancellationToken cancellationToken)
        {
            await using var stream = request.Image.OpenPadded();
            var progress = new Progress<long>(done => context.ReportBytes(done, request.Image.PaddedLengthBytes));
            var digest = await ImageDigest.ComputeAsync(stream, progress, cancellationToken: cancellationToken).ConfigureAwait(false);
            context.Set(DigestKey, digest);
        }

        public async Task WriteAsync(JobContext context, CancellationToken cancellationToken)
        {
            _results = await BurnSupport.BurnInParallelAsync(
                request.Drives,
                request.Image.SectorCount,
                context,
                (drive, progress) => service.BurnImageAsync(drive, request.Image, request.Options, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            context.TryGet<ImageDigest>(DigestKey, out var digest);
            context.Set(ReportKey, new BurnReport(_results, digest?.Sha256));
            if (BurnSupport.FirstFailure(_results) is { } failure)
            {
                throw failure;
            }

            logger.LogInformation("Image {Image} burned to {Count} disc(s)", request.Image.DisplayName, _results.Count);
        }

        public async Task ReadBackAsync(JobContext context, CancellationToken cancellationToken)
        {
            var digest = context.Get<ImageDigest>(DigestKey);
            var total = digest.Length * _results.Count;
            var verified = new List<DriveBurnResult>();
            foreach (var result in _results)
            {
                using var reader = await BurnSupport.OpenReaderWhenReadyAsync(
                    service,
                    result.Drive,
                    request.Image.SectorCount,
                    readBackAttempts,
                    readBackDelay,
                    cancellationToken).ConfigureAwait(false);

                var before = digest.Length * verified.Count;
                var progress = new Progress<long>(bytes => context.ReportBytes(before + bytes, total, "ReadBack"));
                await ReadBackVerifier.VerifyAsync(reader, digest, progress, cancellationToken).ConfigureAwait(false);

                verified.Add(result with { ReadBackVerified = true });
                context.Set(ReportKey, new BurnReport([.. verified, .. _results.Skip(verified.Count)], digest.Sha256));
            }

            _results = verified;
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
                    // The disc is written and verified; a tray that will not open does not make the job fail.
                    logger.LogWarning(ex, "Could not eject {Drive}", drive.DisplayName);
                }
            }
        }
    }
}
