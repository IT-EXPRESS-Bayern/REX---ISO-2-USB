// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;
using Bootrix.Core.Ipc;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Ipc;

public class RpcServerTests
{
    private sealed record Echo(string Text);

    /// <summary>Hands out prepared streams and counts how often somebody asked for the next client.</summary>
    private sealed class QueueListener : IConnectionListener
    {
        private readonly Channel<Stream> _streams = Channel.CreateUnbounded<Stream>();
        private int _acceptCalls;

        public int AcceptCalls => Volatile.Read(ref _acceptCalls);

        public async Task<RpcConnection> AddClientAsync()
        {
            var (server, client) = await PipePair.CreateAsync();
            await _streams.Writer.WriteAsync(server);
            var connection = new RpcConnection(client);
            connection.Start();
            return connection;
        }

        public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acceptCalls);
            return await _streams.Reader.ReadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static RpcSessionFactory EchoSessions() => (_, handlers) =>
    {
        handlers.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p));
        return null;
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout.Default;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition was not reached in time");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ServesAClientAndStopsOnCancellation()
    {
        var listener = new QueueListener();
        var server = new RpcServer(listener, EchoSessions());
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        await using var client = await listener.AddClientAsync();
        var answer = await client.InvokeAsync<Echo>("echo", new Echo("hi")).Within();
        await cts.CancelAsync();

        Assert.Equal("hi", answer.Text);
        Assert.Equal(RpcServerStopReason.Canceled, await run.Within());
        await client.Completion.Within();
    }

    [Fact]
    public async Task OneClientAtATime_TheNextIsNotAcceptedBeforeTheFirstLeaves()
    {
        var listener = new QueueListener();
        var server = new RpcServer(listener, EchoSessions(), new RpcServerOptions { MaxClients = 1 });
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        var first = await listener.AddClientAsync();
        await first.InvokeAsync<Echo>("echo", new Echo("one")).Within();
        await using var second = await listener.AddClientAsync();
        var waiting = second.InvokeAsync<Echo>("echo", new Echo("two"));

        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, listener.AcceptCalls);
        Assert.Equal(1, server.ActiveClients);

        await first.DisposeAsync();

        Assert.Equal("two", (await waiting.Within()).Text);
        await cts.CancelAsync();
        await run.Within();
    }

    [Fact]
    public async Task SeveralClients_AreServedAtTheSameTimeWhenAllowed()
    {
        var listener = new QueueListener();
        var server = new RpcServer(listener, EchoSessions(), new RpcServerOptions { MaxClients = 2 });
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        await using var first = await listener.AddClientAsync();
        await using var second = await listener.AddClientAsync();
        var answers = await Task.WhenAll(
            first.InvokeAsync<Echo>("echo", new Echo("a")),
            second.InvokeAsync<Echo>("echo", new Echo("b"))).Within();

        Assert.Equal(["a", "b"], answers.Select(a => a.Text));
        Assert.Equal(2, server.ActiveClients);
        await cts.CancelAsync();
        await run.Within();
    }

    [Fact]
    public async Task IdleTimeout_StopsAServerNobodyConnectsTo()
    {
        var time = new FakeTimeProvider();
        var idleCalls = 0;
        var server = new RpcServer(new QueueListener(), EchoSessions(), new RpcServerOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(30),
            OnIdle = () => Interlocked.Increment(ref idleCalls),
            Connection = new RpcConnectionOptions { TimeProvider = time },
        });
        var run = server.RunAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(100);
        Assert.False(run.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(RpcServerStopReason.Idle, await run.Within());
        Assert.Equal(1, idleCalls);
    }

    [Fact]
    public async Task IdleTimeout_DoesNotRunWhileAClientIsConnectedAndStartsAgainAfterItLeft()
    {
        var time = new FakeTimeProvider();
        var listener = new QueueListener();
        var server = new RpcServer(listener, EchoSessions(), new RpcServerOptions
        {
            IdleTimeout = TimeSpan.FromSeconds(30),
            Connection = new RpcConnectionOptions { TimeProvider = time },
        });
        var run = server.RunAsync(CancellationToken.None);

        var client = await listener.AddClientAsync();
        await client.InvokeAsync<Echo>("echo", new Echo("hi")).Within();
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);
        Assert.False(run.IsCompleted);

        await client.DisposeAsync();
        await WaitUntilAsync(() => server.ActiveClients == 0);
        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(100);
        Assert.False(run.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(RpcServerStopReason.Idle, await run.Within());
    }

    [Fact]
    public async Task SessionObject_IsDisposedWhenItsConnectionEnds()
    {
        var disposed = new TaskCompletionSource();
        var listener = new QueueListener();
        var server = new RpcServer(listener, (_, _) => new DelegateDisposable(() => disposed.SetResult()));
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        var client = await listener.AddClientAsync();
        await WaitUntilAsync(() => server.ActiveClients == 1);
        await client.DisposeAsync();

        await disposed.Task.Within();
        await cts.CancelAsync();
        await run.Within();
    }

    [Fact]
    public async Task FailingSessionFactory_DropsThatClientAndKeepsServing()
    {
        var listener = new QueueListener();
        var attempts = 0;
        var server = new RpcServer(listener, (connection, handlers) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new InvalidOperationException("no session for you");
            }

            return EchoSessions()(connection, handlers);
        });
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        var rejected = await listener.AddClientAsync();
        await rejected.Completion.Within();
        await using var accepted = await listener.AddClientAsync();

        Assert.Equal("ok", (await accepted.InvokeAsync<Echo>("echo", new Echo("ok")).Within()).Text);
        await cts.CancelAsync();
        await run.Within();
        await rejected.DisposeAsync();
    }

    [Fact]
    public async Task Stopping_AbortsRunningCallsAndWaitsForTheirCleanup()
    {
        var started = new TaskCompletionSource();
        var cleanedUp = false;
        var listener = new QueueListener();
        var server = new RpcServer(listener, (_, handlers) =>
        {
            handlers.Add<Echo, Echo>("job", async (p, context) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, context.AbortToken);
                    return p;
                }
                finally
                {
                    await Task.Delay(100);
                    cleanedUp = true;
                }
            });
            return null;
        });
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        await using var client = await listener.AddClientAsync();
        var call = client.InvokeAsync<Echo>("job", new Echo("x"));
        await started.Task.Within();
        await cts.CancelAsync();
        await run.Within();

        Assert.True(cleanedUp);
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => call.Within());
    }

    private sealed class DelegateDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
