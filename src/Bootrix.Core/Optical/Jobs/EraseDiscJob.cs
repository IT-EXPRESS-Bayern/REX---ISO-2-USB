// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Optical.Jobs;

public sealed record EraseDiscRequest
{
    public required OpticalDrive Drive { get; init; }

    public EraseMode Mode { get; init; } = EraseMode.Quick;

    public bool EjectWhenDone { get; init; }
}

/// <summary>Erases a rewritable disc (CD-RW, DVD±RW, BD-RE). A drive cannot be told to stop in the middle of an erase, so cancelling only works before it starts.</summary>
public sealed class EraseDiscJob(IOpticalService service, ILogger<EraseDiscJob> logger)
{
    public IJob Create(EraseDiscRequest request)
    {
        var run = new Run(service, logger, request);
        return new Job(
            "erase-" + Guid.NewGuid().ToString("N")[..8],
            request.Drive.DisplayName,
            [
                new DelegateJobStep("Erase.Check", 1, run.CheckAsync),
                new DelegateJobStep("Erase.Run", 97, run.EraseAsync),
                new DelegateJobStep("Erase.Finish", 2, run.FinishAsync),
            ]);
    }

    private sealed class Run(IOpticalService service, ILogger logger, EraseDiscRequest request)
    {
        public async Task CheckAsync(JobContext context, CancellationToken cancellationToken)
        {
            if (!request.Drive.CanRecord)
            {
                throw new BootrixException(ErrorCode.NoRecorder, request.Drive.DisplayName);
            }

            var media = await service.QueryMediaAsync(request.Drive, cancellationToken).ConfigureAwait(false);
            if (!media.IsPresent)
            {
                throw new BootrixException(ErrorCode.MediaNotSupported, $"no disc in {request.Drive.DisplayName}");
            }

            if (!media.IsRewritable || media.Condition == OpticalMediaCondition.NotWritable || !request.Drive.Capabilities.CanWrite(media.Type))
            {
                throw new BootrixException(ErrorCode.MediaNotSupported, $"{media.Type} cannot be erased in {request.Drive.DisplayName}");
            }
        }

        public async Task EraseAsync(JobContext context, CancellationToken cancellationToken)
        {
            var progress = new Progress<EraseProgress>(p => context.ReportStep(p.Fraction));
            await service.EraseAsync(request.Drive, request.Mode, progress, cancellationToken).ConfigureAwait(false);
        }

        public async Task FinishAsync(JobContext context, CancellationToken cancellationToken)
        {
            var media = await service.QueryMediaAsync(request.Drive, cancellationToken).ConfigureAwait(false);
            if (media.IsPresent && media.Condition != OpticalMediaCondition.Blank)
            {
                logger.LogWarning("The disc in {Drive} is not reported as blank after the erase ({State})", request.Drive.DisplayName, media.State);
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
    }
}
