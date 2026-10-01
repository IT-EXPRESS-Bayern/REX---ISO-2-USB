// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Writing.Dos;

namespace Bootrix.Windows.Writing.Dos;

/// <summary>
/// "Format drive": partitions the device, formats it with the file system and label of the plan and leaves it empty. This is the data
/// stick of the plan, without any boot code; Windows formats what Bootrix cannot (NTFS, exFAT, ...).
/// </summary>
public sealed class FormatOnlyWriter(WriteServices services) : IMediaWriter
{
    public string Id => "format-only";

    public bool CanWrite(MediaPlan plan, ImageProfile image) => DosMedium.IsPlainFormat(plan, image);

    public IReadOnlyList<IJobStep> CreateSteps(MediaWriteContext context)
    {
        if (context.Plan.Superfloppy)
        {
            return
            [
                StandardSteps.Check(services, context),
                DosSteps.Superfloppy(services, state: null, context),
                StandardSteps.Finish(services, context),
            ];
        }

        return
        [
            StandardSteps.Check(services, context),
            StandardSteps.Prepare(services, context),
            StandardSteps.Finish(services, context),
        ];
    }
}
