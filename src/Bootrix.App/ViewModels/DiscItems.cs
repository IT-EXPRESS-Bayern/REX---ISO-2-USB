// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical;
using Bootrix.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bootrix.App.ViewModels;

public enum DiscAction
{
    Burn,
    Rip,
    Erase,
}

public sealed record DiscActionOption(DiscAction Action, string Display);

public sealed record DiscVerifyOption(BurnVerifyLevel Level, string Display);

public sealed record DiscEraseOption(EraseMode Mode, string Display);

/// <summary>A drive in the list; the check box says whether the action applies to it.</summary>
public sealed partial class DriveItem(OpticalDrive drive, OpticalMedia media, DriveDescription description) : ObservableObject
{
    public OpticalDrive Drive { get; } = drive;

    public OpticalMedia Media { get; } = media;

    public string Title { get; } = description.Title;

    public string Details { get; } = description.Details;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isEnabled = true;
}
