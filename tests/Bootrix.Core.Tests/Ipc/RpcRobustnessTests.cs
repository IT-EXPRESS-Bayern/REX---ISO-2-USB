// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Ipc;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Ipc;

/// <summary>A broker listens on a pipe that any process of the user can write to, so whatever arrives must end in a clean close at worst.</summary>
public class RpcRobustnessTests
{
    private static byte[] Frame(string json) => Frame(Encoding.UTF8.GetBytes(json));

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>A started server connection with a method that counts its calls, fed from a raw stream.</summary>
    private static async Task<(RpcConnection Server, Stream Raw, Func<int> Calls)> StartServerAsync(RpcConnectionOptions? options = null)
    {
        var (serverStream, raw) = await PipePair.CreateAsync();
        var calls = 0;
        var server = new RpcConnection(serverStream, options: options);
        server.Handlers.Add<Echo, Echo>("echo", (p, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(p);
        });
        server.Start();
        return (server, raw, () => Volatile.Read(ref calls));
    }

    private sealed record Echo(string Text);

    [Fact]
    public async Task FrameOverTheLimit_EndsTheConnection()
    {
        var (server, raw, _) = await StartServerAsync(new RpcConnectionOptions { MaxFrameBytes = 1024 });
        await using var serverGuard = server;

        await raw.WriteAsync(new byte[] { 0, 0, 1, 0 }); // 65536 bytes announced
        await raw.FlushAsync();

        await server.Completion.Within();
        Assert.IsType<RpcProtocolException>(server.CloseReason);
        await raw.DisposeAsync();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{\"kind\":\"request\"}")]
    [InlineData("{\"kind\":\"request\",\"id\":0,\"method\":\"echo\"}")]
    [InlineData("{\"kind\":\"request\",\"id\":1}")]
    [InlineData("{\"kind\":\"nonsense\",\"id\":1,\"method\":\"echo\"}")]
    [InlineData("{\"kind\":99,\"id\":1,\"method\":\"echo\"}")]
    [InlineData("{\"kind\":\"notification\"}")]
    [InlineData("{\"kind\":\"notification\",\"method\":\"$/cancel\"}")]
    [InlineData("{\"kind\":\"notification\",\"method\":\"$/cancel\",\"params\":\"x\"}")]
    [InlineData("{not json")]
    public async Task MalformedMessage_EndsTheConnectionWithoutCallingAnyHandler(string json)
    {
        var (server, raw, calls) = await StartServerAsync();
        await using var serverGuard = server;

        await raw.WriteAsync(Frame(json));
        await raw.FlushAsync();

        await server.Completion.Within();
        Assert.Equal(0, calls());
        await raw.DisposeAsync();
    }

    [Fact]
    public async Task InvalidUtf8_EndsTheConnection()
    {
        var (server, raw, calls) = await StartServerAsync();
        await using var serverGuard = server;

        await raw.WriteAsync(Frame([0xFF, 0xFE, 0x80, 0xC0, 0x7B]));
        await raw.FlushAsync();

        await server.Completion.Within();
        Assert.Equal(0, calls());
        await raw.DisposeAsync();
    }

    [Fact]
    public async Task DeeplyNestedJson_EndsTheConnection()
    {
        var (server, raw, _) = await StartServerAsync();
        await using var serverGuard = server;

        await raw.WriteAsync(Frame(new string('[', 5000) + new string(']', 5000)));
        await raw.FlushAsync();

        await server.Completion.Within();
        await raw.DisposeAsync();
    }

    [Fact]
    public async Task DuplicateRequestId_EndsTheConnection()
    {
        var (serverStream, raw) = await PipePair.CreateAsync();
        var server = new RpcConnection(serverStream);
        server.Handlers.Add<Echo, Echo>("echo", async (p, context) =>
        {
            await Task.Delay(Timeout.Infinite, context.AbortToken);
            return p;
        });
        server.Start();
        await using var serverGuard = server;

        var request = Frame("{\"kind\":\"request\",\"id\":7,\"method\":\"echo\",\"params\":{\"text\":\"a\"}}");
        await raw.WriteAsync(request);
        await raw.WriteAsync(request);
        await raw.FlushAsync();

        await server.Completion.Within();
        Assert.IsType<RpcProtocolException>(server.CloseReason);
        await raw.DisposeAsync();
    }

    [Fact]
    public async Task ValidRequestAfterANotificationOfUnknownKind_IsStillServed()
    {
        var (server, raw, calls) = await StartServerAsync();
        await using var serverGuard = server;

        await raw.WriteAsync(Frame("{\"kind\":\"notification\",\"method\":\"who-knows\",\"params\":{}}"));
        await raw.WriteAsync(Frame("{\"kind\":\"request\",\"id\":1,\"method\":\"echo\",\"params\":{\"text\":\"hi\"}}"));
        await raw.FlushAsync();

        var reply = await new FrameStream(raw).ReadAsync(default);
        Assert.NotNull(reply);
        Assert.Contains("hi", Encoding.UTF8.GetString(reply), StringComparison.Ordinal);
        Assert.Equal(1, calls());
        await raw.DisposeAsync();
    }

    [Fact]
    public async Task EveryTruncationOfAValidFrame_EndsCleanly()
    {
        var valid = Frame("{\"kind\":\"request\",\"id\":1,\"method\":\"echo\",\"params\":{\"text\":\"hello\"}}");

        for (var cut = 0; cut < valid.Length; cut++)
        {
            var (server, raw, calls) = await StartServerAsync();
            await raw.WriteAsync(valid.AsMemory(0, cut));
            await raw.FlushAsync();
            await raw.DisposeAsync();

            await server.Completion.Within();
            Assert.Equal(0, calls());
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RandomBytes_NeverHangOrCrashTheServer()
    {
        for (var seed = 0; seed < 150; seed++)
        {
            var random = new Random(seed);
            var garbage = new byte[random.Next(1, 4096)];
            random.NextBytes(garbage);

            // Every second round starts with a plausible header so the payload parser gets its share of the noise.
            if (seed % 2 == 0)
            {
                garbage = Frame(garbage);
            }

            var (server, raw, _) = await StartServerAsync();
            try
            {
                await raw.WriteAsync(garbage);
                await raw.FlushAsync();
            }
            catch (IOException)
            {
                // The server already hung up on the first bad bytes.
            }

            await raw.DisposeAsync();
            await server.Completion.Within();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task RandomlyMutatedValidMessages_NeverHangOrCrashTheServer()
    {
        var valid = Frame("{\"kind\":\"request\",\"id\":1,\"method\":\"echo\",\"params\":{\"text\":\"hello\"}}");

        for (var seed = 0; seed < 150; seed++)
        {
            var random = new Random(seed);
            var mutated = (byte[])valid.Clone();
            for (var i = 0; i < random.Next(1, 4); i++)
            {
                mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            }

            var (server, raw, _) = await StartServerAsync(new RpcConnectionOptions { MaxFrameBytes = 4096 });
            try
            {
                await raw.WriteAsync(mutated);
                await raw.FlushAsync();
            }
            catch (IOException)
            {
                // Hung up on already.
            }

            // A mutation can leave a valid message; closing our end then ends the connection like any other.
            await raw.DisposeAsync();
            await server.Completion.Within();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task SilentPeer_IsDroppedAfterTheHeartbeatTimeout()
    {
        var time = new FakeTimeProvider();
        var (serverStream, raw) = await PipePair.CreateAsync();
        await using var rawGuard = raw;
        var server = new RpcConnection(serverStream, options: new RpcConnectionOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            HeartbeatTimeout = TimeSpan.FromSeconds(3),
            TimeProvider = time,
        });
        server.Start();
        await using var serverGuard = server;

        for (var i = 0; i < 200 && !server.Completion.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(10);
        }

        Assert.True(server.Completion.IsCompleted);
        Assert.IsType<TimeoutException>(server.CloseReason);
    }

    [Fact]
    public async Task PeersThatPingEachOther_StayConnectedWhileOthersGoSilent()
    {
        var time = new FakeTimeProvider();
        var options = new RpcConnectionOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
            TimeProvider = time,
        };
        await using var pair = await RpcPair.CreateAsync(options: options);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(100);
        }

        Assert.False(pair.Server.IsClosed);
        Assert.False(pair.Client.IsClosed);
    }
}
