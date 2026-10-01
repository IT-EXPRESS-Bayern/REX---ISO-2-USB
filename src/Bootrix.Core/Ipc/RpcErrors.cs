// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Ipc;

/// <summary>Moves errors across the connection as code, arguments and technical text, and back to an exception the catalog can describe.</summary>
internal static class RpcErrors
{
    public static WireError ToWire(Exception exception) => exception switch
    {
        BootrixException known => new WireError(
            (int)known.Code,
            [.. known.Arguments.Select(Stringify)],
            known.Detail),
        OperationCanceledException => new WireError((int)ErrorCode.Canceled, null, null),
        _ => new WireError((int)ErrorCode.Unknown, null, $"{exception.GetType().Name}: {exception.Message}"),
    };

    public static Exception FromWire(WireError error)
    {
        var code = Enum.IsDefined(typeof(ErrorCode), error.Code) ? (ErrorCode)error.Code : ErrorCode.Unknown;
        if (code == ErrorCode.Canceled)
        {
            return new OperationCanceledException(error.Detail);
        }

        return new BootrixException(code, error.Detail) { Arguments = [.. error.Arguments ?? []] };
    }

    private static string Stringify(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}

internal static class RpcMethods
{
    /// <summary>Asks the peer to stop a call at the next safe point.</summary>
    public const string Cancel = "$/cancel";

    /// <summary>Asks the peer to stop a call immediately; the call is expected to clean up and answer.</summary>
    public const string Abort = "$/abort";

    public const string Ping = "$/ping";

    public static bool IsReserved(string method) => method.StartsWith("$/", StringComparison.Ordinal);
}
