// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Writing.Dos;

namespace Bootrix.Windows.Writing.Dos;

/// <summary>
/// DOS sticks and diskettes with FreeDOS or MS-DOS. A stick gets an MBR, a FAT partition with the boot sector of the DOS and the system
/// files; a diskette (or a stick that is meant to be started like one) gets a FAT volume at LBA 0. Nothing of the image is copied.
/// </summary>
public sealed class DosWriter(WriteServices services) : IMediaWriter
{
    public string Id => "dos";

    public bool CanWrite(MediaPlan plan, ImageProfile image) => DosMedium.IsDosMedium(plan, image);

    public IReadOnlyList<IJobStep> CreateSteps(MediaWriteContext context)
    {
        var state = new DosJobState();
        var prepareSystem = DosSteps.PrepareSystem(state, context);

        if (context.Plan.Superfloppy)
        {
            return
            [
                prepareSystem,
                StandardSteps.Check(services, context),
                DosSteps.Superfloppy(services, state, context),
                StandardSteps.Finish(services, context),
            ];
        }

        return
        [
            prepareSystem,
            StandardSteps.Check(services, context),
            StandardSteps.Prepare(services, context, (partition, options) => CustomizeFat(state, context, partition, options)),
            DosSteps.CopyFiles(state, context),
            DosSteps.WriteMbr(context),
            StandardSteps.Finish(services, context),
        ];
    }

    /// <summary>The main partition gets the boot code of the DOS; any other partition of a plan (there is none today) is left alone.</summary>
    private static FatFormatOptions CustomizeFat(DosJobState state, MediaWriteContext write, PlannedPartition partition, FatFormatOptions options)
    {
        if (partition.Role != PartitionRole.Main)
        {
            return options;
        }

        var target = DosJobState.TargetOf(write, partition);
        return state.SystemOf(target).Customize(options, target.Plan.TotalSectors);
    }
}
