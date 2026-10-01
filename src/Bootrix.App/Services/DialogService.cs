// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.App.Dialogs;
using Bootrix.Core.Localization;
using Microsoft.Win32;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace Bootrix.App.Services;

public sealed class DialogService(IContentDialogService dialogs, Localizer localizer) : IDialogService
{
    public string? PickImage(string? startDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = localizer.Get("Write.Image"),
            Filter = localizer.Get("Write.ImageFilter"),
            CheckFileExists = true,
        };

        if (!string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory))
        {
            dialog.InitialDirectory = startDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public async Task<bool> ConfirmEraseAsync(IReadOnlyList<EraseTarget> targets)
    {
        var rows = targets.Select(t => new ConfirmRow(
            t.Description.Title,
            t.Description.SizeText,
            localizer.Get("Write.Confirm.Type", t.Description.ConfirmationText),
            t.Device)).ToList();

        var dialog = new EraseConfirmDialog(rows, localizer);
        var result = await dialogs.ShowAsync(dialog, CancellationToken.None);
        return result == ContentDialogResult.Primary;
    }
}
