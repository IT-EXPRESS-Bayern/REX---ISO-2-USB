// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Presentation;
using Bootrix.Core.Storage;

namespace Bootrix.App.Services;

public sealed record EraseTarget(StorageDevice Device, DeviceDescription Description);

public interface IDialogService
{
    /// <summary>Lets the user pick an image file; null when the dialog is dismissed.</summary>
    string? PickImage(string? startDirectory);

    /// <summary>
    /// Asks for confirmation before data is destroyed. Each disk has to be confirmed by typing its own
    /// serial number, so the dialog cannot be dismissed on autopilot.
    /// </summary>
    Task<bool> ConfirmEraseAsync(IReadOnlyList<EraseTarget> targets);
}
