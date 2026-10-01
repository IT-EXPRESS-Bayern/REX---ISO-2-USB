// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bootrix.App.Dialogs;

/// <summary>One disk in the erase confirmation; <see cref="Matches"/> turns true once its confirmation text is typed.</summary>
public sealed partial class ConfirmRow(string title, string sizeText, string prompt, StorageDevice device) : ObservableObject
{
    public string Title { get; } = title;

    public string SizeText { get; } = sizeText;

    public string Prompt { get; } = prompt;

    public bool Matches => DeviceConfirmation.Matches(device, Typed);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Matches))]
    private string _typed = "";
}
