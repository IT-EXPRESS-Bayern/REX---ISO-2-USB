// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;

namespace Bootrix.Core.Engine;

/// <summary>
/// Serializable outcome of a job. Exceptions do not survive the trip through the broker, so the
/// error is reduced to its code and arguments, which is all the localized message needs.
/// </summary>
public sealed record EngineJobResult
{
    public required JobOutcome Outcome { get; init; }

    public TimeSpan Duration { get; init; }

    public string? FailedStep { get; init; }

    public ErrorCode? ErrorCode { get; init; }

    public IReadOnlyList<string> ErrorArguments { get; init; } = [];

    /// <summary>Technical text for the log, never shown as the primary message.</summary>
    public string? ErrorDetail { get; init; }

    /// <summary>SHA-256 of the written image, set by jobs that write an image.</summary>
    public string? ImageSha256 { get; init; }

    public long ImageBytes { get; init; }

    public bool Succeeded => Outcome == JobOutcome.Succeeded;

    public static EngineJobResult From(JobResult result)
    {
        var known = result.Error as BootrixException;
        return new EngineJobResult
        {
            Outcome = result.Outcome,
            Duration = result.Duration,
            FailedStep = result.FailedStep,
            ErrorCode = result.Error is null ? null : known?.Code ?? Errors.ErrorCode.Unknown,
            ErrorArguments = [.. known?.Arguments.Select(a => Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) ?? "") ?? []],
            ErrorDetail = result.Error?.Message,
        };
    }

    /// <summary>Rebuilds an exception that the error catalog can describe; null for a successful job.</summary>
    public Exception? ToException() => Outcome switch
    {
        JobOutcome.Succeeded => null,
        JobOutcome.Canceled => new OperationCanceledException(),
        _ => new BootrixException(ErrorCode ?? Errors.ErrorCode.Unknown, ErrorDetail) { Arguments = [.. ErrorArguments] },
    };
}
