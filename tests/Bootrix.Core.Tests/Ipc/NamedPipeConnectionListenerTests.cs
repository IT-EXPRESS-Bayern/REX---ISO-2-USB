// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using Bootrix.Core.Ipc;

namespace Bootrix.Core.Tests.Ipc;

public class NamedPipeConnectionListenerTests
{
    private sealed record Echo(string Text);

    private sealed class RefusingListener(string name, int refuse) : NamedPipeConnectionListener(name)
    {
        private int _refused;

        protected override bool Authorize(NamedPipeServerStream pipe) => Interlocked.Increment(ref _refused) > refuse;
    }

    [Fact]
    public async Task AcceptedClient_TalksToTheServerOverTheRealPipe()
    {
        var name = PipePair.NewName();
        await using var listener = new NamedPipeConnectionListener(name);
        var accept = listener.AcceptAsync(default);
        await using var client = new RpcConnection(await PipePair.ConnectAsync(name));
        client.Start();

        await using var server = new RpcConnection(await accept.Within());
        server.Handlers.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p));
        server.Start();

        Assert.Equal("over the pipe", (await client.InvokeAsync<Echo>("echo", new Echo("over the pipe")).Within()).Text);
    }

    [Fact]
    public async Task RefusedClient_IsDisconnectedAndTheListenerWaitsForTheNext()
    {
        var name = PipePair.NewName();
        await using var listener = new RefusingListener(name, refuse: 1);
        var accept = listener.AcceptAsync(default);

        var refusedStream = await PipePair.ConnectAsync(name);
        await using var refused = new RpcConnection(refusedStream);
        refused.Start();
        await refused.Completion.Within();
        Assert.False(accept.IsCompleted);

        var acceptedStream = await PipePair.ConnectAsync(name);
        await using var server = new RpcConnection(await accept.Within());
        server.Handlers.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p));
        server.Start();
        await using var client = new RpcConnection(acceptedStream);
        client.Start();

        Assert.Equal("second try", (await client.InvokeAsync<Echo>("echo", new Echo("second try")).Within()).Text);
    }

    [Fact]
    public async Task WaitingForAClient_CanBeCancelled()
    {
        await using var listener = new NamedPipeConnectionListener(PipePair.NewName());
        using var cts = new CancellationTokenSource();
        var accept = listener.AcceptAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accept.Within());
    }

    [Fact]
    public async Task ServerOnRealPipes_ServesOneClientAfterTheOther()
    {
        var name = PipePair.NewName();
        await using var listener = new NamedPipeConnectionListener(name);
        var server = new RpcServer(listener, (_, handlers) =>
        {
            handlers.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p));
            return null;
        });
        using var cts = new CancellationTokenSource();
        var run = server.RunAsync(cts.Token);

        for (var i = 0; i < 3; i++)
        {
            await using var client = new RpcConnection(await PipePair.ConnectAsync(name));
            client.Start();
            Assert.Equal($"client {i}", (await client.InvokeAsync<Echo>("echo", new Echo($"client {i}")).Within()).Text);
        }

        await cts.CancelAsync();
        await run.Within();
    }
}
