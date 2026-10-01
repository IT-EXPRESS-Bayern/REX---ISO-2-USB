// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Presentation;
using Bootrix.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bootrix.App.ViewModels;

public sealed partial class DeviceItem(StorageDevice device, DeviceDescription description) : ObservableObject
{
    public StorageDevice Device { get; } = device;

    public DeviceDescription Description { get; } = description;

    public bool CanSelect => Description.Selectable;

    public bool IsBlocked => !Description.Selectable;

    public bool HasWarning => Description.Warning is not null;

    [ObservableProperty]
    private bool _isSelected;
}
