// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Jobs;

public class JobRunnerTests
{
    private static JobRunner CreateRunner() => new(NullLogger<JobRunner>.Instance);

    [Fact]
    public async Task RunsStepsInOrderAndReportsWeightedProgress()
    {
        var order = new List<string>();
        var job = new Job("j1", "test",
        [
            new DelegateJobStep("a", 1, (_, _) => { order.Add("a"); return Task.CompletedTask; }),
            new DelegateJobStep("b", 3, (ctx, _) =>
            {
                order.Add("b");
                ctx.ReportStep(0.5);
                return Task.CompletedTask;
            }),
        ]);

        var reports = new List<ProgressReport>();
        var result = await CreateRunner().RunAsync(job, new DelegateProgressSink(reports.Add));

        Assert.True(result.Succeeded);
        Assert.Equal(["a", "b"], order);

        // half way through step b: (1 + 3 * 0.5) / 4
        Assert.Contains(reports, r => r.StepKey == "b" && Math.Abs(r.OverallFraction - 0.625) < 1e-9);
        Assert.Equal(1.0, reports[^1].OverallFraction, 6);
    }

    [Fact]
    public async Task CleanupsRunInReverseOrderEvenAfterFailure()
    {
        var cleaned = new List<int>();
        var job = new Job("j2", "test",
        [
            new DelegateJobStep("one", 1, (ctx, _) => { ctx.OnCleanup(() => cleaned.Add(1)); return Task.CompletedTask; }),
            new DelegateJobStep("two", 1, (ctx, _) => { ctx.OnCleanup(() => cleaned.Add(2)); throw new InvalidOperationException("boom"); }),
        ]);

        var result = await CreateRunner().RunAsync(job);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal("two", result.FailedStep);
        Assert.Equal([2, 1], cleaned);
    }

    [Fact]
    public async Task FailingCleanupDoesNotStopTheOthers()
    {
        var cleaned = false;
        var job = new Job("j3", "test",
        [
            new DelegateJobStep("one", 1, (ctx, _) =>
            {
                ctx.OnCleanup(() => cleaned = true);
                ctx.OnCleanup(() => throw new IOException("cannot unlock"));
                return Task.CompletedTask;
            }),
        ]);

        var result = await CreateRunner().RunAsync(job);

        Assert.True(result.Succeeded);
        Assert.True(cleaned);
    }

    [Fact]
    public async Task CancellationYieldsCanceledOutcome()
    {
        using var cts = new CancellationTokenSource();
        var job = new Job("j4", "test",
        [
            new DelegateJobStep("wait", 1, async (_, ct) =>
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }),
        ]);

        var result = await CreateRunner().RunAsync(job, cancellationToken: cts.Token);

        Assert.Equal(JobOutcome.Canceled, result.Outcome);
    }

    [Fact]
    public async Task ValuesArePassedBetweenSteps()
    {
        var seen = 0;
        var job = new Job("j5", "test",
        [
            new DelegateJobStep("set", 1, (ctx, _) => { ctx.Set("disk", 7); return Task.CompletedTask; }),
            new DelegateJobStep("get", 1, (ctx, _) => { seen = ctx.Get<int>("disk"); return Task.CompletedTask; }),
        ]);

        await CreateRunner().RunAsync(job);

        Assert.Equal(7, seen);
    }

    [Fact]
    public async Task ByteProgressProducesSpeedAndEta()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var runner = new JobRunner(NullLogger<JobRunner>.Instance, time);
        var job = new Job("j6", "test",
        [
            new DelegateJobStep("copy", 1, (ctx, _) =>
            {
                ctx.ReportBytes(0, 1000);
                time.Advance(TimeSpan.FromSeconds(1));
                ctx.ReportBytes(100, 1000);
                return Task.CompletedTask;
            }),
        ]);

        var reports = new List<ProgressReport>();
        await runner.RunAsync(job, new DelegateProgressSink(reports.Add));

        var withSpeed = reports.First(r => r.BytesPerSecond > 0);
        Assert.Equal(100, withSpeed.BytesPerSecond, 1);
        Assert.Equal(TimeSpan.FromSeconds(9), withSpeed.Eta!.Value);
    }
}
