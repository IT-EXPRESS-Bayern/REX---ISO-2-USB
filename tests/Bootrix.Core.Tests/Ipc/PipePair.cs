// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using Bootrix.Core.Ipc;

namespace Bootrix.Core.Tests.Ipc;

/// <summary>Two ends of a real named pipe; on Linux this is a Unix domain socket, which is what the production code uses there as well.</summary>
internal static class PipePair
{
    public static string NewName() => "bootrix-test-" + Guid.NewGuid().ToString("N");

    public static async Task<(Stream Server, Stream Client)> CreateAsync()
    {
        var name = NewName();
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(10_000));
        return (server, client);
    }

    public static async Task<NamedPipeClientStream> ConnectAsync(string name)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(10_000);
        return client;
    }
}

/// <summary>Two connected <see cref="RpcConnection"/>s, started, with the handlers of each side set up by the caller.</summary>
internal sealed class RpcPair : IAsyncDisposable
{
    private RpcPair(RpcConnection server, RpcConnection client)
    {
        Server = server;
        Client = client;
    }

    public RpcConnection Server { get; }

    public RpcConnection Client { get; }

    public static async Task<RpcPair> CreateAsync(
        Action<RpcHandlerTable>? serverHandlers = null,
        Action<RpcHandlerTable>? clientHandlers = null,
        RpcConnectionOptions? options = null)
    {
        var (serverStream, clientStream) = await PipePair.CreateAsync();
        var server = new RpcConnection(serverStream, options: options);
        var client = new RpcConnection(clientStream, options: options);
        serverHandlers?.Invoke(server.Handlers);
        clientHandlers?.Invoke(client.Handlers);
        server.Start();
        client.Start();
        return new RpcPair(server, client);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}

internal static class TestTimeout
{
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(15);

    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(Default);

    public static Task Within(this Task task) => task.WaitAsync(Default);
}
