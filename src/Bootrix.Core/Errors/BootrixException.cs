// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Errors;

public class BootrixException : Exception
{
    public BootrixException(ErrorCode code, string? detail = null, Exception? inner = null)
        : base(detail ?? code.ToString(), inner)
    {
        Code = code;
        Detail = detail;
    }

    public ErrorCode Code { get; }

    /// <summary>Technical detail for the log; never shown as the primary message.</summary>
    public string? Detail { get; }

    /// <summary>Values substituted into the localized cause and action texts.</summary>
    public IReadOnlyList<object?> Arguments { get; init; } = [];
}
