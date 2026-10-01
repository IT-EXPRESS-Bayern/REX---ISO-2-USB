// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Writing.Windows.Customization;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Windows.Customization;

/// <summary>
/// Sets the LabConfig values in the registry of the Setup image (boot.wim), so that Windows 11 Setup accepts the
/// hardware before it reads any answer file. The answer file carries the same values as well; this is the part that
/// still works when the image's own answer file is kept, when Setup is started with an explicit /unattend switch, or
/// when the answer file cannot be read.
/// </summary>
public sealed class BootWimBypassCustomizer(BootImagePatcher patcher, ILogger<BootWimBypassCustomizer> logger) : IWindowsMediaCustomizer
{
    /// <summary>The first Windows 11 build. Setup of Windows 10 has no hardware check that LabConfig could switch off.</summary>
    private const int FirstWindows11Build = 22000;

    public string Id => "boot-wim-bypass";

    public bool Applies(MediaWriteContext write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var build = write.Image.WindowsBuild;
        return write.Image.Kind == ImageKind.WindowsSetup
            && HardwareCheckBypass.IsRequested(write.Spec.Windows)
            && (build == 0 || build >= FirstWindows11Build);
    }

    public async Task ApplyAsync(WindowsMediaCustomization customization, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customization);
        ArgumentNullException.ThrowIfNull(progress);

        var changes = HardwareCheckBypass.Changes(customization.Write.Spec.Windows);
        var patched = await patcher.PatchAsync(
            Path.Combine(customization.MediaRoot, "sources", "boot.wim"),
            changes,
            customization.WorkDirectory,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (!patched)
        {
            logger.LogWarning("The Setup image was not patched; the hardware check bypass relies on the answer file only");
        }

        progress.Report(1);
    }
}
