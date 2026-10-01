// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Jobs;

public class SpeedEstimatorTests
{
    [Fact]
    public void ConstantRateConvergesToRate()
    {
        var time = new FakeTimeProvider();
        var estimator = new SpeedEstimator(time);
        long bytes = 0;
        estimator.Update(bytes);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            bytes += 50_000_000;
            estimator.Update(bytes);
        }

        Assert.Equal(50_000_000, estimator.BytesPerSecond, 1);
    }

    [Fact]
    public void ReactsToSlowdown()
    {
        var time = new FakeTimeProvider();
        var estimator = new SpeedEstimator(time, halfLifeSeconds: 2);
        long bytes = 0;
        estimator.Update(bytes);

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            bytes += 100;
            estimator.Update(bytes);
        }

        for (var i = 0; i < 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            bytes += 10;
            estimator.Update(bytes);
        }

        Assert.InRange(estimator.BytesPerSecond, 10, 20);
    }

    [Fact]
    public void NoEtaWithoutSpeedOrWhenFinished()
    {
        var estimator = new SpeedEstimator(new FakeTimeProvider());

        Assert.Null(estimator.Remaining(0, 100));
    }

    [Fact]
    public void IgnoresSamplesCloserThanFiftyMilliseconds()
    {
        var time = new FakeTimeProvider();
        var estimator = new SpeedEstimator(time);
        estimator.Update(0);
        time.Advance(TimeSpan.FromMilliseconds(10));
        estimator.Update(1_000_000);

        Assert.Equal(0, estimator.BytesPerSecond);
    }
}
