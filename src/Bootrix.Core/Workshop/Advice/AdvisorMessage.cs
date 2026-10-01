// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;

namespace Bootrix.Core.Workshop.Advice;

public enum AdvisorSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>
/// A localizable statement: a resource key and its arguments. Arguments are strings or numbers so the record
/// serializes to JSON unchanged; numbers are formatted in the language of the localizer that renders the text.
/// </summary>
public sealed record AdvisorMessage(string Key, AdvisorSeverity Severity, params object?[] Arguments)
{
    public string Describe(Localizer? localizer = null) => (localizer ?? Localizer.Default).Get(Key, Arguments);
}
