// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Writing.Raw;
using Bootrix.Windows.Jobs;

namespace Bootrix.Windows.Writing;

/// <summary>
/// Writes the image byte for byte (hybrid ISOs, raw disk images, Apple images, compressed images of all of these); the
/// image brings its own partition table and boot code. A persistence partition goes behind the image once it is written
/// and verified, and the backup GPT of a Mac disk image moves to the end of the disk.
/// </summary>
public sealed class RawCopyWriter(RawWriteJob rawWrite) : IMediaWriter
{
    public string Id => "raw-copy";

    public bool CanWrite(MediaPlan plan, ImageProfile image) => plan.WriteMethod == WriteMethod.RawCopy;

    public IReadOnlyList<IJobStep> CreateSteps(MediaWriteContext context)
    {
        var persistence = context.Targets.Select(PersistenceOf).ToList();
        if (persistence.Any(request => request is not null) && PersistencePreflight.Problem(context.Inspection.Layout) is { } problem)
        {
            // Better told now than after the image has been written.
            throw new BootrixException(ErrorCode.PersistenceLayoutUnsupported, problem) { Arguments = [problem] };
        }

        var macGpt = context.Image.Kind == ImageKind.Apple && context.Inspection.Layout is { HasGpt: true, GptHeaderValid: true };
        var job = rawWrite.Create(new RawWriteRequest
        {
            ImagePath = context.ImagePath,
            Targets = [.. context.Targets.Select((t, i) => new RawWriteTargetRequest(t.Device, t.Identity, persistence[i], macGpt))],
            Verify = context.Spec.Verify.ReadBack,
            ArchiveEntry = context.ArchiveEntry,
            BlockMap = context.BlockMap,
        });

        return job.Steps;
    }

    private static PersistenceRequest? PersistenceOf(MediaWriteTarget target) =>
        target.Plan.NeedsPersistencePartition && target.Plan.Partitions.FirstOrDefault(p => p.Role == PartitionRole.Persistence) is { } partition
            ? PersistenceRequest.FromPlan(partition)
            : null;
}
