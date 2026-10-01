// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;

namespace Bootrix.Core.Planning;

/// <summary>
/// Something the user should know before writing, as a resource key plus the values for its
/// placeholders. Nothing here is translated until it is shown.
/// </summary>
public sealed record PlanWarning
{
    public PlanWarning(string code, params object?[] args)
    {
        Code = code;
        Args = args;
    }

    public string Code { get; }

    public IReadOnlyList<object?> Args { get; }

    public string Format(Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Get(Code, [.. Args]);
    }
}
