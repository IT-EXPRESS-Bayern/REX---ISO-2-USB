// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Writing.Windows.Customization;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Windows.Customization;

/// <summary>
/// Puts the boot manager signed by the Windows UEFI CA 2023 on the medium, for machines whose firmware has revoked
/// the Windows Production PCA 2011 or never trusted the older one. The files come from the image itself; where it
/// has none the medium keeps what it has, unless the job asked for the new signature explicitly.
/// </summary>
public sealed class Ca2023BootManagerCustomizer(Ca2023BootManagerSwap swap, ILogger<Ca2023BootManagerCustomizer> logger) : IWindowsMediaCustomizer
{
    public string Id => "boot-manager-2023";

    public bool Applies(MediaWriteContext write)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (write.Image.Kind is not (ImageKind.WindowsSetup or ImageKind.WindowsPe))
        {
            return false;
        }

        // Nothing for a BIOS to see: the boot manager files are only used by UEFI firmware.
        if (write.Plan.Firmware == TargetFirmware.Bios)
        {
            return false;
        }

        return Ca2023Policy.Decide(write.Spec.Windows.BootCertificate, write.Image.WindowsBuild) != Ca2023Mode.Off;
    }

    public async Task ApplyAsync(WindowsMediaCustomization customization, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customization);
        ArgumentNullException.ThrowIfNull(progress);

        var mode = Ca2023Policy.Decide(customization.Write.Spec.Windows.BootCertificate, customization.Build);
        var result = await swap.ApplyAsync(customization.MediaRoot, customization.WorkDirectory, mode, customization.Build, progress, cancellationToken).ConfigureAwait(false);
        if (!result.Applied)
        {
            logger.LogWarning("The medium keeps the boot manager of the image: {Reason}", result.SkippedReason);
        }

        progress.Report(1);
    }
}
