// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;

namespace Bootrix.Core.Errors;

public sealed record ErrorDescription(string Code, string Cause, string Action);

public static class ErrorCatalog
{
    public static string FormatCode(ErrorCode code) => $"BX{(int)code:D4}";

    public static ErrorDescription Describe(Exception exception, Localizer? localizer = null)
    {
        localizer ??= Localizer.Default;

        return exception switch
        {
            BootrixException bx => Describe(bx.Code, localizer, [.. bx.Arguments]),
            OperationCanceledException => Describe(ErrorCode.Canceled, localizer),
            _ => Describe(ErrorCode.Unknown, localizer),
        };
    }

    public static ErrorDescription Describe(ErrorCode code, Localizer localizer, params object?[] args)
    {
        var key = $"Error.{code}";
        var causeKey = $"{key}.Cause";
        if (!localizer.Has(causeKey))
        {
            code = ErrorCode.Unknown;
            key = $"Error.{code}";
            args = [];
        }

        return new ErrorDescription(
            FormatCode(code),
            localizer.Get($"{key}.Cause", args),
            localizer.Get($"{key}.Action", args));
    }
}
