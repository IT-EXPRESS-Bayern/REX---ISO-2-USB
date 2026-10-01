// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Jobs;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>
/// Writes Windows setup media from an ISO: FAT32 with a split install.wim, or NTFS and exFAT with the UEFI:NTFS
/// helper partition, on MBR or GPT, for UEFI, BIOS or both. The files are copied (not the image written byte for
/// byte), so the stick can be larger than the image and the install image can be changed afterwards.
/// </summary>
public sealed class WindowsSetupWriter : IMediaWriter
{
    public const string SourceKey = "Write.Windows.Source";
    public const string CopyKey = "Write.Windows.Copy";
    public const string BootCodeKey = "Write.Windows.BootCode";
    public const string VerifyKey = "Write.Windows.Verify";
    public const string CustomizeKey = "Write.Customize";

    private readonly WriteServices _services;
    private readonly IImageStreamProvider _images;
    private readonly IReadOnlyList<IWindowsMediaCustomizer> _customizers;
    private readonly IVbrCodeSource _bootCode;
    private readonly ITargetOps _ops;

    /// <param name="customizers">Changes after the copy; the default is <see cref="WindowsCustomizers.CreateDefault"/>.</param>
    /// <param name="bootCode">Where the BIOS boot code of a FAT32 medium comes from; the default lets Windows format a small virtual disk once.</param>
    public WindowsSetupWriter(
        WriteServices services,
        IImageStreamProvider images,
        IReadOnlyList<IWindowsMediaCustomizer>? customizers = null,
        IVbrCodeSource? bootCode = null)
        : this(services, images, customizers, bootCode, null)
    {
    }

    internal WindowsSetupWriter(
        WriteServices services,
        IImageStreamProvider images,
        IReadOnlyList<IWindowsMediaCustomizer>? customizers,
        IVbrCodeSource? bootCode,
        ITargetOps? ops)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(images);
        _services = services;
        _images = images;
        _customizers = customizers ?? WindowsCustomizers.CreateDefault(services);
        _bootCode = bootCode ?? new CachingVbrCodeSource(new ReferenceVolumeVbrSource(services.LoggerFor<ReferenceVolumeVbrSource>()));
        _ops = ops ?? new PhysicalTargetOps(services);
    }

    public string Id => "windows-setup";

    /// <summary>Setup media and WinPE media in file-copy mode. Windows To Go applies the image instead and has its own writer.</summary>
    public bool CanWrite(MediaPlan plan, ImageProfile image)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(image);
        return plan.WriteMethod == WriteMethod.ExtractFiles
            && !plan.Superfloppy
            && image.Kind is ImageKind.WindowsSetup or ImageKind.WindowsPe;
    }

    /// <summary>The state the steps of one write share; only the customizers that apply to this job take part.</summary>
    internal WindowsSetupRun CreateRun(MediaWriteContext context) =>
        new(_services, _images, _bootCode, [.. _customizers.Where(customizer => customizer.Applies(context))], _ops, context);

    public IReadOnlyList<IJobStep> CreateSteps(MediaWriteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = CreateRun(context);

        var steps = new List<IJobStep>
        {
            StandardSteps.Check(_services, context),
            new DelegateJobStep(SourceKey, 3, run.OpenSourceAsync),
            StandardSteps.Prepare(_services, context, run.CustomizeFat, extraPayloads: run.ExtraPayloads),
            new DelegateJobStep(CopyKey, 60, run.CopyAsync),
        };

        if (context.Targets.Any(target => WindowsMbr.IsNeeded(target.Plan)))
        {
            steps.Add(new DelegateJobStep(BootCodeKey, 1, run.WriteBootCodeAsync));
        }

        if (context.Spec.Verify.ReadBack)
        {
            steps.Add(new DelegateJobStep(VerifyKey, 25, run.VerifyAsync));
        }

        if (run.Customizers.Count > 0)
        {
            steps.Add(new DelegateJobStep(CustomizeKey, 5, run.CustomizeAsync));
        }

        steps.Add(StandardSteps.Finish(_services, context));
        return steps;
    }
}
