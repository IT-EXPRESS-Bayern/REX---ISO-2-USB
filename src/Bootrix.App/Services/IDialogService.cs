// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Presentation;
using Bootrix.Core.Storage;

namespace Bootrix.App.Services;

public sealed record EraseTarget(StorageDevice Device, DeviceDescription Description);

public interface IDialogService
{
    /// <summary>Lets the user pick an image file; null when the dialog is dismissed.</summary>
    string? PickImage(string? startDirectory);

    /// <summary>Lets the user pick a Windows ISO; null when the dialog is dismissed.</summary>
    string? PickIso(string? startDirectory);

    /// <summary>Lets the user choose where an ISO file is saved; asks before an existing file is replaced. Null when dismissed.</summary>
    string? PickSaveIso(string? startDirectory, string suggestedName);

    /// <summary>
    /// Asks for confirmation before data is destroyed. Each disk has to be confirmed by typing its own
    /// serial number, so the dialog cannot be dismissed on autopilot.
    /// </summary>
    Task<bool> ConfirmEraseAsync(IReadOnlyList<EraseTarget> targets);

    /// <summary>A plain yes/no question with the given button text for "yes".</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText);

    /// <summary>Opens a web page in the default browser; only https addresses are accepted.</summary>
    void OpenUrl(string url);
}
