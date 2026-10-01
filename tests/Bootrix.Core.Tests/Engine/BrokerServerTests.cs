// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Ipc;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Engine;

/// <summary>The pieces the elevated process is made of, wired the way it wires them: a listener, a server, one host per client.</summary>
public class BrokerServerTests
{
    private static async Task<BrokerEngineClient> ConnectAsync(string pipeName, byte[]? secret)
    {
        var stream = await PipePair.ConnectAsync(pipeName);
        return await BrokerEngineClient.ConnectAsync(stream, new BrokerClientOptions { Secret = secret }).Within();
    }

    [Fact]
    public async Task ClientListsDisksRunsAJobAndShutsTheBrokerDown()
    {
        var name = PipePair.NewName();
        var secret = RandomNumberGenerator.GetBytes(32);
        var engine = new FakeEngine { Devices = [TestPaths.SampleDevice()] };
        using var stop = new CancellationTokenSource();
        await using var listener = new NamedPipeConnectionListener(name);
        var server = new RpcServer(
            listener,
            (connection, _) => new BrokerEngineHost(engine, connection, new BrokerEngineHostOptions { Secret = secret, OnShutdownRequested = stop.Cancel }));
        var run = server.RunAsync(stop.Token);

        var client = await ConnectAsync(name, secret);
        var disks = await client.ListDisksAsync(new DiskFilter(), default).Within();
        var result = await client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default).Within();
        await client.DisposeAsync();

        Assert.Single(disks);
        Assert.True(result.Succeeded);
        Assert.Equal(RpcServerStopReason.Canceled, await run.Within());
    }

    [Fact]
    public async Task ClientWithTheWrongSecret_IsRefusedAndTheNextOneIsStillServed()
    {
        var name = PipePair.NewName();
        var secret = RandomNumberGenerator.GetBytes(32);
        using var stop = new CancellationTokenSource();
        await using var listener = new NamedPipeConnectionListener(name);
        var server = new RpcServer(
            listener,
            (connection, _) => new BrokerEngineHost(new FakeEngine(), connection, new BrokerEngineHostOptions { Secret = secret }));
        var run = server.RunAsync(stop.Token);

        await Assert.ThrowsAsync<BootrixException>(() => ConnectAsync(name, RandomNumberGenerator.GetBytes(32)));
        var client = await ConnectAsync(name, secret);

        Assert.True(client.IsConnected);
        await client.DisposeAsync();
        await stop.CancelAsync();
        await run.Within();
    }

    [Fact]
    public async Task BrokerThatNobodyConnectsTo_StopsAfterTheIdleTimeout()
    {
        var time = new FakeTimeProvider();
        await using var listener = new NamedPipeConnectionListener(PipePair.NewName());
        var server = new RpcServer(
            listener,
            (connection, _) => new BrokerEngineHost(new FakeEngine(), connection),
            new RpcServerOptions { IdleTimeout = TimeSpan.FromSeconds(30), Connection = new RpcConnectionOptions { TimeProvider = time } });
        var run = server.RunAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(RpcServerStopReason.Idle, await run.Within());
    }

    [Fact]
    public async Task ClientThatDiesMidJob_LeavesAnAbortedJobAndAnIdleBroker()
    {
        var time = new FakeTimeProvider();
        var name = PipePair.NewName();
        var started = new TaskCompletionSource();
        var cleaned = new TaskCompletionSource();
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
                    cleaned.SetResult();
                }

                return new EngineJobResult { Outcome = JobOutcome.Canceled };
            },
        };
        await using var listener = new NamedPipeConnectionListener(name);
        var server = new RpcServer(
            listener,
            (connection, _) => new BrokerEngineHost(engine, connection),
            new RpcServerOptions { IdleTimeout = TimeSpan.FromSeconds(30), Connection = new RpcConnectionOptions { TimeProvider = time } });
        var run = server.RunAsync(CancellationToken.None);

        var stream = await PipePair.ConnectAsync(name);
        var client = await BrokerEngineClient.ConnectAsync(stream).Within();
        var job = client.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default);
        await started.Task.Within();
        await stream.DisposeAsync();

        await cleaned.Task.Within();
        var result = await job.Within();
        for (var i = 0; i < 500 && server.ActiveClients > 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, server.ActiveClients);
        time.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(ErrorCode.BrokerDisconnected, result.ErrorCode);
        Assert.Equal(RpcServerStopReason.Idle, await run.Within());
        await client.DisposeAsync();
    }
}
