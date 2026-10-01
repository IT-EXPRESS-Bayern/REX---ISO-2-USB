// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Writing.Windows.Customization;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Windows.Customization;

/// <summary>
/// Gets the driver folders of the job onto the medium. They are copied to <c>$WinPEDriver$</c>, which Setup loads
/// into WinPE and also installs into the new system. Only when the job asks for it are they added to boot.wim and
/// install.wim with DISM as well: that rewrites the images on the stick, takes minutes per edition and is of use
/// only when a driver has to be in place before Setup starts or without Setup's scheduling.
/// </summary>
public sealed class DriverInjectionCustomizer(
    IUserContext user,
    StagedImageEditor editor,
    IDriverServicing servicing,
    ILogger<DriverInjectionCustomizer> logger) : IWindowsMediaCustomizer
{
    public string Id => "drivers";

    public bool Applies(MediaWriteContext write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return write.Image.Kind == ImageKind.WindowsSetup && write.Spec.Windows.DriverFolders.Any(folder => !string.IsNullOrWhiteSpace(folder));
    }

    public async Task ApplyAsync(WindowsMediaCustomization customization, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(customization);
        ArgumentNullException.ThrowIfNull(progress);

        var windows = customization.Write.Spec.Windows;
        var plan = windows.InjectDriversIntoImages
            ? OfflineInjectionPlan.Create(customization.MediaRoot, windows.Edition)
            : new OfflineInjectionPlan([], []);
        foreach (var reason in plan.Skipped)
        {
            logger.LogWarning("Drivers are not added offline: {Reason}", reason);
        }

        var stages = new StagedProgress(progress, [2, .. plan.Targets.Select(t => t.Description == "boot.wim" ? 3.0 : 6.0)]);

        var stager = new DriverStager(user, logger: logger);
        var staged = await stager.StageAsync(windows.DriverFolders, customization.MediaRoot, stages.Stage(0), cancellationToken).ConfigureAwait(false);
        stages.Complete(0);

        var injector = new OfflineDriverInjector(editor, servicing, logger);
        for (var i = 0; i < plan.Targets.Count; i++)
        {
            var added = await injector.InjectAsync(plan.Targets[i], staged.InfFiles, customization.WorkDirectory, stages.Stage(i + 1), cancellationToken).ConfigureAwait(false);
            stages.Complete(i + 1);
            logger.LogInformation("{Image}: {Added} driver package(s) added offline", plan.Targets[i].Description, added);
        }

        progress.Report(1);
    }
}
