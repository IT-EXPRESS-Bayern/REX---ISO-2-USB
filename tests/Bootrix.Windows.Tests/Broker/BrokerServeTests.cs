// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Security.Cryptography;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Windows.Broker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Broker;

/// <summary>The loop of the broker with a fake engine, over a real pipe: what the process does between starting and exiting.</summary>
public class BrokerServeTests
{
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    private static string NewPipeName() => "bootrix-test-" + Guid.NewGuid().ToString("N");

    private static async Task<(BrokerEngineClient Client, NamedPipeClientStream Stream)> ConnectAsync(string pipe, byte[]? secret = null)
    {
        var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(10_000);
        var client = await BrokerEngineClient.ConnectAsync(stream, new BrokerClientOptions { Secret = secret ?? Secret }).Within();
        return (client, stream);
    }

    private static Task<RpcServerStopReason> Serve(NamedPipeConnectionListener listener, FakeEngine engine, BrokerServeSettings settings, CancellationToken token) =>
        BrokerHost.ServeAsync(listener, engine, Secret, settings, NullLogger.Instance, token);

    [Fact]
    public async Task ClientListsDisksRunsAJobAndShutsTheBrokerDown()
    {
        var pipe = NewPipeName();
        await using var listener = new NamedPipeConnectionListener(pipe);
        var engine = new FakeEngine { Devices = [TestDisks.Device(3, TestDisks.Disk3)] };
        var serving = Serve(listener, engine, new BrokerServeSettings(), CancellationToken.None);

        var (client, _) = await ConnectAsync(pipe);
        var disks = await client.ListDisksAsync(new DiskFilter(), default).Within();
        var result = await client.RunJobAsync(TestDisks.Request(TestDisks.Device(3, TestDisks.Disk3)), new SyncProgress(), default).Within();
        await client.DisposeAsync();

        Assert.Single(disks);
        Assert.True(result.Succeeded);
        Assert.Equal(RpcServerStopReason.Canceled, await serving.Within());
    }

    [Fact]
    public async Task ClientThatDoesNotKnowTheSecret_IsRefused()
    {
        var pipe = NewPipeName();
        await using var listener = new NamedPipeConnectionListener(pipe);
        var engine = new FakeEngine();
        using var stop = new CancellationTokenSource();
        var serving = Serve(listener, engine, new BrokerServeSettings(), stop.Token);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ConnectAsync(pipe, RandomNumberGenerator.GetBytes(32)));

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        Assert.Equal(0, engine.JobsStarted);
        await stop.CancelAsync();
        await serving.Within();
    }

    [Fact]
    public async Task BrokerThatNobodyConnectsTo_StopsAfterTheIdleTimeout()
    {
        await using var listener = new NamedPipeConnectionListener(NewPipeName());

        var reason = await Serve(listener, new FakeEngine(), new BrokerServeSettings { IdleTimeout = TimeSpan.FromMilliseconds(300) }, CancellationToken.None).Within();

        Assert.Equal(RpcServerStopReason.Idle, reason);
    }

    [Fact]
    public async Task BrokerWaitsForANewClientAfterTheFirstOneLeft_ButOnlyForTheIdleTimeout()
    {
        var pipe = NewPipeName();
        await using var listener = new NamedPipeConnectionListener(pipe);
        var serving = Serve(listener, new FakeEngine(), new BrokerServeSettings { IdleTimeout = TimeSpan.FromSeconds(30) }, CancellationToken.None);

        // Leaving without the shutdown message, as a crashed GUI does.
        var (first, firstStream) = await ConnectAsync(pipe);
        await firstStream.DisposeAsync();
        await first.Disconnected.Within();
        var (second, _) = await ConnectAsync(pipe);

        Assert.True(second.IsConnected);
        Assert.False(serving.IsCompleted);
        await second.DisposeAsync();
        Assert.Equal(RpcServerStopReason.Canceled, await serving.Within());
    }

    [Fact]
    public async Task JobThatIgnoresTheAbort_EndsTheProcessAfterTheGracePeriod()
    {
        var pipe = NewPipeName();
        await using var listener = new NamedPipeConnectionListener(pipe);
        var stuck = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var fired = new TaskCompletionSource();
        var engine = new FakeEngine
        {
            Job = async (_, _, _, _) =>
            {
                started.SetResult();
                await stuck.Task;
                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        using var stop = new CancellationTokenSource();
        var serving = Serve(
            listener,
            engine,
            new BrokerServeSettings { AbortGrace = TimeSpan.FromMilliseconds(200), OnAbortStuck = () => fired.TrySetResult() },
            stop.Token);

        var (client, _) = await ConnectAsync(pipe);
        using var abort = new CancellationTokenSource();
        var run = client.RunJobAsync(TestDisks.Request(TestDisks.Device(3, TestDisks.Disk3)), new SyncProgress(), default, abort.Token);
        await started.Task.Within();
        await abort.CancelAsync();

        await fired.Task.Within();
        stuck.SetResult();
        await run.Within();
        await stop.CancelAsync();
        await serving.Within();
    }

    [Fact]
    public async Task ClientThatDiesMidJob_AbortsItAndTheBrokerCleansUpBeforeItGoesIdle()
    {
        var pipe = NewPipeName();
        await using var listener = new NamedPipeConnectionListener(pipe);
        var started = new TaskCompletionSource();
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
                finally
                {
                    await Task.Delay(100);
                    cleaned = true;
                }

                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        var serving = Serve(listener, engine, new BrokerServeSettings { IdleTimeout = TimeSpan.FromMilliseconds(500) }, CancellationToken.None);

        var (client, stream) = await ConnectAsync(pipe);
        var run = client.RunJobAsync(TestDisks.Request(TestDisks.Device(3, TestDisks.Disk3)), new SyncProgress(), default);
        await started.Task.Within();
        await stream.DisposeAsync();

        var result = await run.Within();
        var reason = await serving.Within();

        Assert.True(cleaned);
        Assert.Equal(ErrorCode.BrokerDisconnected, result.ErrorCode);
        Assert.Equal(RpcServerStopReason.Idle, reason);
        await client.DisposeAsync();
    }

    [Fact]
    public async Task StoppingTheBroker_AbortsARunningJob()
    {
        var pipe = NewPipeName();
        await using var listener = new NamedPipeConnectionListener(pipe);
        var started = new TaskCompletionSource();
        var aborted = new TaskCompletionSource();
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
                    aborted.SetResult();
                }

                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        using var stop = new CancellationTokenSource();
        var serving = Serve(listener, engine, new BrokerServeSettings(), stop.Token);

        var (client, _) = await ConnectAsync(pipe);
        var run = client.RunJobAsync(TestDisks.Request(TestDisks.Device(3, TestDisks.Disk3)), new SyncProgress(), default);
        await started.Task.Within();
        await stop.CancelAsync();

        await aborted.Task.Within();
        await serving.Within();
        Assert.Equal(ErrorCode.BrokerDisconnected, (await run.Within()).ErrorCode);
        await client.DisposeAsync();
    }
}
