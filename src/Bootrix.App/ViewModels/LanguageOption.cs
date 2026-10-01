// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.App.ViewModels;

/// <summary>An entry of the language list; an empty code means "follow Windows".</summary>
public sealed record LanguageOption(string Code, string Display);

public sealed record ThemeOption(Bootrix.Core.Settings.AppTheme Theme, string Display);
