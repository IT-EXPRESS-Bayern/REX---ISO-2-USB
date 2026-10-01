// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Ipc;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Engine;

public class BrokerEngineTests
{
    [Fact]
    public async Task ListDisks_ReturnsTheDevicesAndPassesTheFilter()
    {
        var device = TestPaths.SampleDevice();
        await using var pair = await BrokerPair.CreateAsync(new FakeEngine { Devices = [device, TestPaths.SampleDevice(4, TestPaths.Disk4)] });

        var disks = await pair.Client.ListDisksAsync(new DiskFilter { IncludeUsbHardDisks = true, IncludeBlocked = false }, default).Within();

        Assert.Equal(2, disks.Count);
        Assert.Equivalent(device, disks[0]);
        Assert.Equal(DeviceProtection.WriteProtected | DeviceProtection.Offline, disks[0].Protection);
        Assert.Equal(new DiskExtent(3, 1_048_576, 15_000_000_000), disks[0].Volumes[0].Extents[0]);
        Assert.Equal("E:", disks[0].DriveLetters);
        Assert.True(pair.Engine.LastFilter!.IncludeUsbHardDisks);
        Assert.False(pair.Engine.LastFilter.IncludeBlocked);
    }

    [Fact]
    public async Task CaptureIdentity_ReturnsTheFingerprint()
    {
        await using var pair = await BrokerPair.CreateAsync();

        var identity = await pair.Client.CaptureIdentityAsync(TestPaths.Disk3, default).Within();

        Assert.Equal(TestPaths.IdentityOf(TestPaths.Disk3), identity);
    }

    [Fact]
    public async Task EngineError_ReachesTheCallerWithItsCodeAndArguments()
    {
        var engine = new FakeEngine
        {
            Capture = path => throw new BootrixException(ErrorCode.DeviceNotFound, path) { Arguments = ["Disk 3"] },
        };
        await using var pair = await BrokerPair.CreateAsync(engine);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => pair.Client.CaptureIdentityAsync(TestPaths.Disk3, default).Within());

        Assert.Equal(ErrorCode.DeviceNotFound, ex.Code);
        Assert.Equal(["Disk 3"], ex.Arguments);
    }

    [Fact]
    public async Task CaptureIdentity_RefusesPathsThatAreNoDisks()
    {
        await using var pair = await BrokerPair.CreateAsync();

        var ex = await Assert.ThrowsAsync<BootrixException>(() => pair.Client.CaptureIdentityAsync(@"C:\Windows\System32\config\SAM", default).Within());

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public async Task RunJob_ForwardsProgressInOrderAndDeliversItBeforeTheResult()
    {
        var engine = new FakeEngine
        {
            Job = (_, progress, _, _) =>
            {
                for (var i = 1; i <= 200; i++)
                {
                    progress.Report(FakeEngine.Report(i, 200));
                }

                return Task.FromResult(new EngineJobResult
                {
                    Outcome = JobOutcome.Succeeded,
                    Duration = TimeSpan.FromSeconds(12),
                    ImageSha256 = "abcd",
                    ImageBytes = 4_000_000_000,
                });
            },
        };
        await using var pair = await BrokerPair.CreateAsync(engine);
        var progress = new SyncProgress();

        var result = await pair.Client.RunJobAsync(TestPaths.ValidRequest(), progress, default).Within();

        Assert.True(result.Succeeded);
        Assert.Equal("abcd", result.ImageSha256);
        Assert.Equal(4_000_000_000, result.ImageBytes);
        Assert.Equal(TimeSpan.FromSeconds(12), result.Duration);

        // The pump may skip reports when the pipe is slower than the job, but never reorders them, and the last one always gets through.
        var seen = progress.Reports;
        Assert.NotEmpty(seen);
        Assert.Equal(seen.OrderBy(r => r.BytesDone), seen);
        Assert.Equal(200, seen[^1].BytesDone);
        Assert.Equal("Raw.Write", seen[^1].StepKey);
    }

    [Fact]
    public async Task RunJob_DeliversTheRequestAsTheEngineExpectsIt()
    {
        EngineJobRequest? received = null;
        var engine = new FakeEngine
        {
            Job = (request, _, _, _) =>
            {
                received = request;
                return Task.FromResult(new EngineJobResult { Outcome = JobOutcome.Succeeded });
            },
        };
        await using var pair = await BrokerPair.CreateAsync(engine);

        await pair.Client.RunJobAsync(TestPaths.ValidRequest(TestPaths.Image, TestPaths.Disk3, TestPaths.Disk4) with { Verify = false }, new SyncProgress(), default).Within();

        var write = Assert.IsType<RawWriteJobRequest>(received);
        Assert.Equal(TestPaths.Image, write.ImagePath);
        Assert.False(write.Verify);
        Assert.Equal([TestPaths.Disk3, TestPaths.Disk4], write.Targets.Select(t => t.DevicePath));
        Assert.Equal(TestPaths.IdentityOf(TestPaths.Disk4), write.Targets[1].Identity);
    }

    [Fact]
    public async Task RunJob_SoftCancelStopsTheJobAtItsOwnPaceWithoutTheAbortToken()
    {
        var started = new TaskCompletionSource();
        var abortSeen = false;
        var cleaned = false;
        var engine = new FakeEngine
        {
            Job = async (_, _, cancel, abort) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancel);
                }
                catch (OperationCanceledException)
                {
                    await Task.Delay(50);
                    abortSeen = abort.IsCancellationRequested;
                    cleaned = true;
                }

                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        await using var pair = await BrokerPair.CreateAsync(engine);
        using var soft = new CancellationTokenSource();

        var run = pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), soft.Token);
        await started.Task.Within();
        await soft.CancelAsync();
        var result = await run.Within();

        Assert.Equal(JobOutcome.Canceled, result.Outcome);
        Assert.True(cleaned);
        Assert.False(abortSeen);
    }

    [Fact]
    public async Task RunJob_AbortReachesTheJobAsTheHardToken()
    {
        var started = new TaskCompletionSource();
        var engine = new FakeEngine
        {
            Job = async (_, _, cancel, abort) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, abort);
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }

                return new EngineJobResult { Outcome = cancel.IsCancellationRequested ? JobOutcome.Canceled : JobOutcome.Failed };
            },
        };
        await using var pair = await BrokerPair.CreateAsync(engine);
        using var soft = new CancellationTokenSource();
        using var hard = new CancellationTokenSource();

        var run = pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), soft.Token, hard.Token);
        await started.Task.Within();
        await hard.CancelAsync();

        Assert.Equal(JobOutcome.Canceled, (await run.Within()).Outcome);
    }

    [Fact]
    public async Task RunJob_AlreadyCancelled_NeverReachesTheEngine()
    {
        await using var pair = await BrokerPair.CreateAsync();

        var result = await pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), new CancellationToken(true)).Within();

        Assert.Equal(JobOutcome.Canceled, result.Outcome);
        Assert.Equal(0, pair.Engine.JobsStarted);
    }

    [Fact]
    public async Task ClientVanishingMidJob_AbortsTheJobAndTheBrokerWaitsForItsCleanup()
    {
        var started = new TaskCompletionSource();
        var abortSeen = false;
        var cleaned = false;
        var engine = new FakeEngine
        {
            Job = async (_, _, _, abort) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, abort);
                }
                catch (OperationCanceledException)
                {
                    abortSeen = true;
                }
                finally
                {
                    await Task.Delay(150);
                    cleaned = true;
                }

                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        var pair = await BrokerPair.CreateAsync(engine);

        var run = pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default);
        await started.Task.Within();
        await pair.ClientStream.DisposeAsync();

        var result = await run.Within();
        await pair.ServerConnection.Completion.Within();

        Assert.True(abortSeen);
        Assert.True(cleaned);
        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.BrokerDisconnected, result.ErrorCode);
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task BrokerVanishingMidJob_GivesTheCallerAnUnmistakableError()
    {
        var started = new TaskCompletionSource();
        var engine = new FakeEngine
        {
            Job = async (_, _, _, abort) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, abort).ContinueWith(_ => { }, TaskScheduler.Default);
                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        var pair = await BrokerPair.CreateAsync(engine);

        var run = pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default);
        await started.Task.Within();
        await pair.ServerConnection.DisposeAsync();
        var result = await run.Within();

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.BrokerDisconnected, result.ErrorCode);
        var exception = Assert.IsType<BootrixException>(result.ToException());
        Assert.Equal("BX8103", ErrorCatalog.Describe(exception).Code);
        Assert.False(pair.Client.IsConnected);
        await pair.Client.Disconnected.Within();
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task RunJob_AfterTheBrokerIsGone_FailsWithBrokerDisconnected()
    {
        var pair = await BrokerPair.CreateAsync();
        await pair.ServerConnection.DisposeAsync();
        await pair.Client.Disconnected.Within();

        var result = await pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default).Within();
        var ex = await Assert.ThrowsAsync<RpcConnectionClosedException>(() => pair.Client.ListDisksAsync(new DiskFilter(), default));

        Assert.Equal(ErrorCode.BrokerDisconnected, result.ErrorCode);
        Assert.Equal(ErrorCode.BrokerDisconnected, ex.Code);
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task EngineThrowing_BecomesAFailedResultInsteadOfBreakingTheConnection()
    {
        var engine = new FakeEngine { Job = (_, _, _, _) => throw new InvalidOperationException("engine exploded") };
        await using var pair = await BrokerPair.CreateAsync(engine);

        var result = await pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default).Within();

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.Unknown, result.ErrorCode);
        Assert.True(pair.Client.IsConnected);
        Assert.Equal(TestPaths.Disk3, (await pair.Client.CaptureIdentityAsync(TestPaths.Disk3, default).Within()).DevicePath);
    }

    [Theory]
    [InlineData(@"relative\image.iso")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"C:\a\..\b.iso")]
    [InlineData("")]
    public async Task RunJob_InvalidRequestsAreRefusedBeforeTheEngineSeesThem(string imagePath)
    {
        await using var pair = await BrokerPair.CreateAsync();

        var result = await pair.Client.RunJobAsync(TestPaths.ValidRequest(imagePath), new SyncProgress(), default).Within();

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.InvalidSpec, result.ErrorCode);
        Assert.Equal(0, pair.Engine.JobsStarted);
    }

    [Fact]
    public async Task RunJob_TargetsThatAreNoDisksAreRefused()
    {
        await using var pair = await BrokerPair.CreateAsync();
        var request = new RawWriteJobRequest
        {
            ImagePath = TestPaths.Image,
            Targets = [new EngineTarget(@"\\.\PhysicalDrive0", TestPaths.IdentityOf(@"\\.\PhysicalDrive0"))],
        };

        var result = await pair.Client.RunJobAsync(request, new SyncProgress(), default).Within();

        Assert.Equal(ErrorCode.InvalidSpec, result.ErrorCode);
        Assert.Equal(0, pair.Engine.JobsStarted);
    }

    [Fact]
    public async Task TwoJobsAtOnce_EachGetOnlyTheirOwnProgress()
    {
        var gate = new TaskCompletionSource();
        var engine = new FakeEngine
        {
            Job = async (request, progress, _, _) =>
            {
                var write = (RawWriteJobRequest)request;
                var total = write.Targets.Count * 1000L;
                progress.Report(FakeEngine.Report(total, total));
                await gate.Task;
                return new EngineJobResult { Outcome = JobOutcome.Succeeded, ImageBytes = total };
            },
        };
        await using var pair = await BrokerPair.CreateAsync(engine);
        var first = new SyncProgress();
        var second = new SyncProgress();

        var runOne = pair.Client.RunJobAsync(TestPaths.ValidRequest(TestPaths.Image, TestPaths.Disk3), first, default);
        var runTwo = pair.Client.RunJobAsync(TestPaths.ValidRequest(TestPaths.Image, TestPaths.Disk3, TestPaths.Disk4), second, default);
        for (var i = 0; i < 300 && (first.Reports.Count == 0 || second.Reports.Count == 0); i++)
        {
            await Task.Delay(10);
        }

        gate.SetResult();
        await Task.WhenAll(runOne, runTwo).Within();

        Assert.Equal(1000, first.Reports[^1].BytesTotal);
        Assert.Equal(2000, second.Reports[^1].BytesTotal);
    }

    [Fact]
    public async Task DevicesChanged_IsForwardedToTheClient()
    {
        await using var pair = await BrokerPair.CreateAsync();
        var raised = new TaskCompletionSource();
        pair.Client.DevicesChanged += (_, _) => raised.TrySetResult();

        pair.Engine.RaiseDevicesChanged();

        await raised.Task.Within();
    }

    [Fact]
    public async Task ClosingTheClient_AsksTheBrokerToShutDown()
    {
        var shutdown = new TaskCompletionSource();
        var pair = await BrokerPair.CreateAsync(hostOptions: new BrokerEngineHostOptions { OnShutdownRequested = () => shutdown.TrySetResult() });

        await pair.Client.DisposeAsync();

        await shutdown.Task.Within();
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task AbortThatTheJobIgnores_TriggersTheWatchdogAfterTheGracePeriod()
    {
        var time = new FakeTimeProvider();
        var stuck = new TaskCompletionSource();
        var watchdogFired = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var engine = new FakeEngine
        {
            Job = async (_, _, _, _) =>
            {
                started.SetResult();
                await stuck.Task;
                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        await using var pair = await BrokerPair.CreateAsync(engine, new BrokerEngineHostOptions
        {
            AbortGrace = TimeSpan.FromSeconds(20),
            OnAbortStuck = () => watchdogFired.TrySetResult(),
            TimeProvider = time,
        });
        using var hard = new CancellationTokenSource();

        var run = pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default, hard.Token);
        await started.Task.Within();
        await hard.CancelAsync();
        await Task.Delay(100);
        time.Advance(TimeSpan.FromSeconds(19));
        await Task.Delay(100);
        Assert.False(watchdogFired.Task.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));

        await watchdogFired.Task.Within();
        stuck.SetResult();
        await run.Within();
    }

    [Fact]
    public async Task JobThatStopsInTime_NeverTriggersTheWatchdog()
    {
        var time = new FakeTimeProvider();
        var fired = false;
        var started = new TaskCompletionSource();
        var engine = new FakeEngine
        {
            Job = async (_, _, _, abort) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, abort).ContinueWith(_ => { }, TaskScheduler.Default);
                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        await using var pair = await BrokerPair.CreateAsync(engine, new BrokerEngineHostOptions { OnAbortStuck = () => fired = true, TimeProvider = time });
        using var hard = new CancellationTokenSource();

        var run = pair.Client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default, hard.Token);
        await started.Task.Within();
        await hard.CancelAsync();
        await run.Within();
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(fired);
    }
}
