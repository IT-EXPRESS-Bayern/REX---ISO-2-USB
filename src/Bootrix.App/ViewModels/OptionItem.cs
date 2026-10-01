// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.App.ViewModels;

/// <summary>An entry of a drop-down list: the value that goes into the job and the text the user reads.</summary>
public sealed record OptionItem<T>(T Value, string Display);
