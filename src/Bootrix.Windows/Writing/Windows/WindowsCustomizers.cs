// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Writing.Windows.Customization;
using Bootrix.Windows.Dism;
using Bootrix.Windows.Writing.Windows.Customization;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>The customizers that run, in this order, after the setup files have been copied.</summary>
public static class WindowsCustomizers
{
    /// <summary>
    /// Everything that can fail because of the job or the image comes first and is quick: the answer file
    /// (invalid names) and the boot manager swap (an image without the 2023 files). The edits of boot.wim and
    /// install.wim, which take minutes, come last, so a bad request does not cost a long wait first.
    /// </summary>
    public static IReadOnlyList<IWindowsMediaCustomizer> CreateDefault(WriteServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var files = new ImageFileSystem();
        var editor = new StagedImageEditor(new DismImageServicing(), files, services.LoggerFor<StagedImageEditor>());
        IUserContext user = services.ClientImpersonator is { } client ? new ClientUserContext(client) : new ProcessUserContext();

        return
        [
            new UnattendCustomizer(services.LoggerFor<UnattendCustomizer>()),
            new Ca2023BootManagerCustomizer(
                new Ca2023BootManagerSwap(new WimBootFileExtractor(), logger: services.LoggerFor<Ca2023BootManagerSwap>()),
                services.LoggerFor<Ca2023BootManagerCustomizer>()),
            new BootWimBypassCustomizer(
                new BootImagePatcher(editor, files, services.LoggerFor<BootImagePatcher>()),
                services.LoggerFor<BootWimBypassCustomizer>()),
            new DriverInjectionCustomizer(user, editor, new DismDriverServicing(), services.LoggerFor<DriverInjectionCustomizer>()),
        ];
    }
}
