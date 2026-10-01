// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Windows.Jobs;

namespace Bootrix.Windows.Writing;

/// <summary>Writes the image byte for byte (hybrid ISOs, raw disk images); the image brings its own partition table and boot code.</summary>
public sealed class RawCopyWriter(RawWriteJob rawWrite) : IMediaWriter
{
    public string Id => "raw-copy";

    public bool CanWrite(MediaPlan plan, ImageProfile image) => plan.WriteMethod == WriteMethod.RawCopy;

    public IReadOnlyList<IJobStep> CreateSteps(MediaWriteContext context)
    {
        var job = rawWrite.Create(new RawWriteRequest
        {
            ImagePath = context.ImagePath,
            Targets = [.. context.Targets.Select(t => new RawWriteTargetRequest(t.Device, t.Identity))],
            Verify = context.Spec.Verify.ReadBack,
        });

        return job.Steps;
    }
}
