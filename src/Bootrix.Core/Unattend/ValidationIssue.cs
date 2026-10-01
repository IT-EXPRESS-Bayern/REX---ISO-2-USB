// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;

namespace Bootrix.Core.Unattend;

public sealed record ValidationIssue(string Key, bool IsError = true, params object?[] Arguments)
{
    public string Describe(Localizer? localizer = null) => (localizer ?? Localizer.Default).Get(Key, Arguments);
}
