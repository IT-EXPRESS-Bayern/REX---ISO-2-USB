// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public class StagedProgressTests
{
    [Fact]
    public void Stages_ShareTheRangeByWeight()
    {
        var log = new ProgressLog();
        var progress = new StagedProgress(log, 1, 3);

        progress.Stage(0).Report(1);
        progress.Stage(1).Report(0.5);
        progress.Complete(1);

        Assert.Equal([0.25, 0.625, 1.0], log.Values);
    }

    [Fact]
    public void ValueThatStepsBack_IsNotReported()
    {
        var log = new ProgressLog();
        var progress = new StagedProgress(log, 1, 1);
        var stage = progress.Stage(0);

        stage.Report(0.6);
        stage.Report(0.2);
        stage.Report(0.6);
        stage.Report(0.8);

        Assert.Equal([0.3, 0.4], log.Values);
    }

    [Fact]
    public void OutOfRangeAndNaN_AreClamped()
    {
        var log = new ProgressLog();
        var progress = new StagedProgress(log, 1);

        progress.Stage(0).Report(double.NaN);
        progress.Stage(0).Report(-3);
        progress.Stage(0).Report(7);

        Assert.Equal([1.0], log.Values);
    }

    [Fact]
    public void RangeWithinAStage_IsScaled()
    {
        var log = new ProgressLog();
        var progress = new StagedProgress(log, 1);

        var first = progress.Stage(0, 0, 0.4);
        first.Report(0.5);
        first.Report(1);

        Assert.Equal([0.2, 0.4], log.Values.Select(v => Math.Round(v, 6)));
    }

    [Fact]
    public void CallbacksFromManyThreads_NeverMakeTheValueGoBack()
    {
        var log = new ProgressLog();
        var progress = new StagedProgress(log, 1, 1, 1, 1);

        Parallel.For(0, 4, stage =>
        {
            var reporter = progress.Stage(stage);
            for (var i = 0; i <= 100; i++)
            {
                reporter.Report(i / 100.0);
            }
        });

        log.AssertMonotonicToOne();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void WeightThatIsNotPositive_IsRejected(double weight)
    {
        Assert.Throws<ArgumentException>(() => new StagedProgress(new ProgressLog(), 1, weight));
    }

    [Fact]
    public void StageOutsideTheRange_IsRejected()
    {
        var progress = new StagedProgress(new ProgressLog(), 1, 1);

        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Stage(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Stage(-1));
    }
}
