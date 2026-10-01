// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Windows.Jobs;

namespace Bootrix.Windows.Writing.Linux;

/// <summary>
/// Writes a Linux live or installer ISO in file-copy mode (ISO mode): the stick gets a partition table, a FAT (or NTFS,
/// exFAT) partition with the files of the image, a persistence partition if asked for, and a BIOS boot loader (Syslinux
/// or GRUB) next to the EFI loaders that are copied with the rest. Used when the image is not a hybrid, when the person
/// chose it, or when persistence is wanted.
/// </summary>
public sealed class LinuxIsoWriter(WriteServices services, IImageStreamProvider images) : IMediaWriter
{
    public const string CopyKey = "Write.Linux.Copy";
    public const string BootloaderKey = "Write.Linux.Bootloader";
    public const string VerifyKey = "Write.Linux.Verify";

    public string Id => "linux-iso";

    /// <summary>
    /// Linux images in extract mode, and VMware ESXi, which boots through Syslinux the same way. Other systems that the
    /// image inspector files under "other" (ReactOS, KolibriOS) need loaders Bootrix does not have.
    /// </summary>
    public bool CanWrite(MediaPlan plan, ImageProfile image)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(image);

        if (plan.WriteMethod != WriteMethod.ExtractFiles || plan.Superfloppy || plan.Scheme == PartitionScheme.Auto)
        {
            return false;
        }

        return image.Kind is ImageKind.LinuxHybrid or ImageKind.LinuxIsoOnly
            || (image.Kind == ImageKind.OtherOs && string.Equals(image.Family, "esxi", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<IJobStep> CreateSteps(MediaWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = new LinuxWriteRun(services, images, context);

        var steps = new List<IJobStep>
        {
            StandardSteps.Check(services, context),
            StandardSteps.Prepare(services, context, extraPayloads: run.ExtraPayloads),
            new DelegateJobStep(CopyKey, 70, run.CopyAsync),
            new DelegateJobStep(BootloaderKey, 4, run.InstallBootloaderAsync),
        };

        if (context.Spec.Verify.ReadBack)
        {
            steps.Add(new DelegateJobStep(VerifyKey, 20, run.VerifyAsync));
        }

        steps.Add(StandardSteps.Finish(services, context));
        return steps;
    }
}
