// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;

namespace Bootrix.Core.Images;

public enum WarningSeverity
{
    /// <summary>Worth knowing, nothing needs to be done.</summary>
    Info,

    /// <summary>Boot or usability problems are possible with the chosen settings.</summary>
    Warning,

    /// <summary>The image is damaged or cannot work as it is.</summary>
    Error,
}

/// <summary>
/// A finding about an image. The key names a text in the string resources; the arguments fill its
/// placeholders, so the same finding can be shown in either language and logged with its values.
/// </summary>
public sealed record ImageWarning(string Key, WarningSeverity Severity = WarningSeverity.Warning, params object?[] Arguments)
{
    public string Format(Localizer? localizer = null) => (localizer ?? Localizer.Default).Get(Key, Arguments);
}
