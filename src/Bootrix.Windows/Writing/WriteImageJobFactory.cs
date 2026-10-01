// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Hosting;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing;
using Bootrix.Core.Writing.Raw;
using Bootrix.Windows.Jobs;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing;

/// <summary>
/// Turns a <see cref="WriteImageJobRequest"/> into a job: looks at the image, plans the medium for every
/// target and lets the first writer that can carry out the plan build the steps.
/// </summary>
public sealed class WriteImageJobFactory(
    IDiskService disks,
    MediaPlanService planner,
    IImageStreamProvider images,
    IEnumerable<IMediaWriter> writers,
    BootrixPaths paths,
    ILogger<WriteImageJobFactory> logger)
{
    private readonly IReadOnlyList<IMediaWriter> _writers = [.. writers];

    public async Task<IJob> CreateAsync(WriteImageJobRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Targets.Count == 0)
        {
            throw new ArgumentException("At least one target disk is required.", nameof(request));
        }

        // Opened through the image provider so that the broker looks at the file with the rights of the user who asked for it.
        // Compressed files are looked at as they are, Apple containers by the volume they hold.
        AppleRefinement refined;
        await using (var opened = await images.OpenForInspectionAsync(
            request.ImagePath,
            new ImageOpenOptions { ArchiveEntry = request.ArchiveEntry, BlockMap = request.BlockMap },
            cancellationToken).ConfigureAwait(false))
        {
            var found = await planner.InspectAsync(
                opened.Stream,
                Path.GetFileName(request.ImagePath),
                new ImageInspectOptions { ArchiveEntry = request.ArchiveEntry },
                cancellationToken).ConfigureAwait(false);
            refined = AppleImagePlanning.Refine(found, opened.Stream, opened.Source?.Kind == ImageSourceKind.AppleContainer);
        }

        var inspection = refined.Inspection;
        if (inspection.HasErrors)
        {
            throw new BootrixException(ErrorCode.ImageUnsupported, request.ImagePath) { Arguments = [request.ImagePath] };
        }

        var jobId = "write-" + Guid.NewGuid().ToString("N")[..8];
        var targets = new List<MediaWriteTarget>(request.Targets.Count);
        foreach (var target in request.Targets)
        {
            var device = await Task.Run(() => disks.Find(target.DevicePath), cancellationToken).ConfigureAwait(false)
                ?? throw new BootrixException(ErrorCode.DeviceNotFound, target.DevicePath);
            if (device.IsBlocked)
            {
                throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
            }

            var plan = refined.Apply(MediaPlanService.Plan(inspection, request.Spec.Target, device).Plan);
            targets.Add(new MediaWriteTarget { Device = device, Identity = target.Identity, Plan = plan });
        }

        EnsureSameMethod(targets);

        var writer = _writers.FirstOrDefault(w => w.CanWrite(targets[0].Plan, inspection.Profile))
            ?? throw new BootrixException(ErrorCode.MediaWriterUnavailable, $"{inspection.Profile.Kind}/{targets[0].Plan.WriteMethod}")
            {
                Arguments = [inspection.Profile.Kind.ToString(), targets[0].Plan.WriteMethod.ToString()],
            };

        logger.LogInformation("Job {JobId}: {Kind} image, {Method}, writer {Writer}, {Count} target(s)", jobId, inspection.Profile.Kind, targets[0].Plan.WriteMethod, writer.Id, targets.Count);

        var context = new MediaWriteContext
        {
            JobId = jobId,
            ImagePath = request.ImagePath,
            Inspection = inspection,
            Spec = request.Spec,
            Targets = targets,
            WorkDirectory = Path.Combine(paths.WorkDirectory, jobId),
            LocalAccountPassword = request.LocalAccountPassword,
            ArchiveEntry = request.ArchiveEntry,
            BlockMap = request.BlockMap,
        };

        return new Job(jobId, Path.GetFileName(request.ImagePath), writer.CreateSteps(context));
    }

    /// <summary>Targets of different sizes may get different layouts, but they must all be written the same way.</summary>
    private static void EnsureSameMethod(List<MediaWriteTarget> targets)
    {
        var method = targets[0].Plan.WriteMethod;
        if (targets.Any(t => t.Plan.WriteMethod != method))
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "targets need different write methods")
            {
                Arguments = ["The selected drives would need different write methods; write them in separate jobs."],
            };
        }
    }
}
