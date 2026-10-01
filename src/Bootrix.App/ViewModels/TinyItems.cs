// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bootrix.App.ViewModels;

public sealed record TinyProfileOption(string Id, string Name, string Description, bool BreaksServicing, bool IsWindows11);

public sealed record TinyEditionOption(int Index, string Label);

/// <summary>An option group with its switch. Applied means it is removed or switched off in the built image.</summary>
public sealed partial class TinyGroupItem(TinyGroupOption option) : ObservableObject
{
    public string Id { get; } = option.Id;

    public string Title { get; } = option.Title;

    public string Hint { get; } = option.Hint;

    [ObservableProperty]
    private bool _applied = option.DefaultOn;
}
