// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Jobs;

public readonly record struct ProgressReport(
    string JobId,
    int StepIndex,
    int StepCount,
    string StepKey,
    double StepFraction,
    double OverallFraction,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond,
    TimeSpan? Eta,
    string? Detail);

public interface IProgressSink
{
    void Report(in ProgressReport report);
}

public sealed class NullProgressSink : IProgressSink
{
    public static readonly NullProgressSink Instance = new();

    public void Report(in ProgressReport report)
    {
    }
}

public sealed class DelegateProgressSink(Action<ProgressReport> handler) : IProgressSink
{
    public void Report(in ProgressReport report) => handler(report);
}
