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

    public string? PickIso(string? startDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = localizer.Get("Tiny.Source"),
            Filter = localizer.Get("Tiny.SourceFilter"),
            CheckFileExists = true,
        };

        if (!string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory))
        {
            dialog.InitialDirectory = startDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickDiscImage(string? startDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = localizer.Get("Disc.Image"),
            Filter = localizer.Get("Disc.ImageFilter"),
            CheckFileExists = true,
        };

        if (!string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory))
        {
            dialog.InitialDirectory = startDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSaveIso(string? startDirectory, string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = localizer.Get("Tiny.Output"),
            Filter = localizer.Get("Tiny.OutputFilter"),
            FileName = suggestedName,
            DefaultExt = ".iso",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (!string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory))
        {
            dialog.InitialDirectory = startDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public async Task<string?> PromptAsync(string title, string message, string initialText, string confirmText)
    {
        var input = new System.Windows.Controls.TextBox { Text = initialText, MaxLength = 64, MinWidth = 320, Margin = new System.Windows.Thickness(0, 8, 0, 0) };
        var content = new System.Windows.Controls.StackPanel();
        content.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap });
        content.Children.Add(input);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = confirmText,
            CloseButtonText = localizer.Get("Write.Confirm.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary ? input.Text : null;
    }

    public string? PickFolder(string? startDirectory)
    {
        var dialog = new OpenFolderDialog { Title = localizer.Get("Settings.TeamProfiles") };
        if (!string.IsNullOrEmpty(startDirectory) && Directory.Exists(startDirectory))
        {
            dialog.InitialDirectory = startDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = localizer.Get("Write.Confirm.Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialogs.ShowAsync(dialog, CancellationToken.None) == ContentDialogResult.Primary;
    }

    public void OpenUrl(string url)
    {
        // The address comes from vendor data; anything but https never reaches the shell.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
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
