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

public class BrokerHandshakeTests
{
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);

    /// <summary>A host whose client speaks the wire protocol by hand, so the tests can say things the real client never would.</summary>
    private static async Task<(RpcConnection Raw, RpcConnection Server, FakeEngine Engine)> StartRawAsync(byte[]? secret = null, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var engine = new FakeEngine();
        var (serverStream, clientStream) = await PipePair.CreateAsync();
        var server = new RpcConnection(serverStream, logger: logger);
        _ = new BrokerEngineHost(engine, server, new BrokerEngineHostOptions { Secret = secret }, logger);
        server.Start();
        var raw = new RpcConnection(clientStream, logger: logger);
        raw.Start();
        return (raw, server, engine);
    }

    [Fact]
    public async Task Hello_WithTheRightSecret_OpensTheConnection()
    {
        await using var pair = await BrokerPair.CreateAsync(
            hostOptions: new BrokerEngineHostOptions { Secret = Secret },
            clientOptions: new BrokerClientOptions { Secret = Secret });

        Assert.True(pair.Client.IsConnected);
        Assert.Empty(await pair.Client.ListDisksAsync(new DiskFilter(), default).Within());
    }

    [Fact]
    public async Task Hello_WithAWrongSecret_IsRejectedAndTheConnectionClosed()
    {
        var engine = new FakeEngine();
        var (serverStream, clientStream) = await PipePair.CreateAsync();
        var server = new RpcConnection(serverStream);
        _ = new BrokerEngineHost(engine, server, new BrokerEngineHostOptions { Secret = Secret });
        server.Start();

        var ex = await Assert.ThrowsAsync<BootrixException>(() => BrokerEngineClient.ConnectAsync(
            clientStream,
            new BrokerClientOptions { Secret = RandomNumberGenerator.GetBytes(32) }).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        await server.Completion.Within();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Hello_WithoutASecret_IsRejectedWhenOneIsRequired()
    {
        var (raw, server, _) = await StartRawAsync(Secret);
        await using var rawGuard = raw;
        await using var serverGuard = server;

        var ex = await Assert.ThrowsAsync<BootrixException>(
            () => raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(1, 1, "test", null)).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        await server.Completion.Within();
    }

    [Fact]
    public async Task Hello_WithMalformedSecret_IsRejected()
    {
        var (raw, server, _) = await StartRawAsync(Secret);
        await using var rawGuard = raw;
        await using var serverGuard = server;

        await Assert.ThrowsAsync<BootrixException>(
            () => raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(1, 1, "test", "***not base64***")).Within());

        await server.Completion.Within();
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(0, 0)]
    [InlineData(-3, -1)]
    public async Task Hello_WithVersionsTheBrokerDoesNotSpeak_IsRejectedAndTheConnectionClosed(int min, int max)
    {
        var (raw, server, _) = await StartRawAsync();
        await using var rawGuard = raw;
        await using var serverGuard = server;

        var ex = await Assert.ThrowsAsync<BootrixException>(
            () => raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(min, max, "test", null)).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        Assert.Contains("do not match", ex.Detail, StringComparison.Ordinal);
        await server.Completion.Within();
    }

    [Fact]
    public async Task Hello_ChoosesTheHighestVersionBothSpeak()
    {
        var (raw, server, _) = await StartRawAsync();
        await using var rawGuard = raw;
        await using var serverGuard = server;

        var answer = await raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(0, 99, "test", null)).Within();

        Assert.Equal(BrokerProtocol.CurrentVersion, answer.Version);
        Assert.Equal(AppInfo.Version, answer.Build);
    }

    [Fact]
    public async Task Hello_Twice_EndsTheConnection()
    {
        var (raw, server, _) = await StartRawAsync();
        await using var rawGuard = raw;
        await using var serverGuard = server;
        await raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(1, 1, "test", null)).Within();

        await Assert.ThrowsAsync<BootrixException>(
            () => raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(1, 1, "test", null)).Within());

        await server.Completion.Within();
    }

    [Theory]
    [InlineData(BrokerProtocol.ListDisks)]
    [InlineData(BrokerProtocol.CaptureIdentity)]
    [InlineData(BrokerProtocol.RunJob)]
    public async Task EverythingBeforeTheHello_IsRefusedAndEndsTheConnection(string method)
    {
        var (raw, server, engine) = await StartRawAsync();
        await using var rawGuard = raw;
        await using var serverGuard = server;
        object parameters = method switch
        {
            BrokerProtocol.ListDisks => new ListDisksParams(new DiskFilter()),
            BrokerProtocol.CaptureIdentity => new CaptureIdentityParams(TestPaths.Disk3),
            _ => new RunJobParams("run", TestPaths.ValidRequest()),
        };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => raw.InvokeAsync<object>(method, parameters).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        await server.Completion.Within();
        Assert.Equal(0, engine.JobsStarted);
    }

    [Fact]
    public async Task UnknownMethodAfterTheHello_IsAProtocolErrorButNotFatal()
    {
        await using var pair = await BrokerPair.CreateAsync();
        var (raw, server, _) = await StartRawAsync();
        await using var rawGuard = raw;
        await using var serverGuard = server;
        await raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(1, 1, "test", null)).Within();

        var ex = await Assert.ThrowsAsync<BootrixException>(() => raw.InvokeAsync<object>("formatDisk", new CaptureIdentityParams(TestPaths.Disk3)).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        Assert.False(server.IsClosed);
    }

    [Fact]
    public async Task ClientThatNeverSaysHello_IsDroppedAfterTheTimeout()
    {
        var time = new FakeTimeProvider();
        var (serverStream, clientStream) = await PipePair.CreateAsync();
        var server = new RpcConnection(serverStream);
        using var host = new BrokerEngineHost(new FakeEngine(), server, new BrokerEngineHostOptions { HelloTimeout = TimeSpan.FromSeconds(10), TimeProvider = time });
        server.Start();
        await using var client = new RpcConnection(clientStream);
        client.Start();

        time.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(100);
        Assert.False(server.IsClosed);
        time.Advance(TimeSpan.FromSeconds(2));

        await server.Completion.Within();
        await client.Completion.Within();
    }

    [Fact]
    public async Task ClientThatSaidHello_IsNotDroppedByTheHelloTimeout()
    {
        var time = new FakeTimeProvider();
        var (serverStream, clientStream) = await PipePair.CreateAsync();
        var server = new RpcConnection(serverStream);
        using var host = new BrokerEngineHost(new FakeEngine(), server, new BrokerEngineHostOptions { HelloTimeout = TimeSpan.FromSeconds(10), TimeProvider = time });
        server.Start();
        await using var client = await BrokerEngineClient.ConnectAsync(clientStream).Within();

        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);

        Assert.False(server.IsClosed);
        Assert.True(client.IsConnected);
        await server.DisposeAsync();
    }

    [Fact]
    public async Task RequestOfAnUnknownKind_IsAProtocolErrorAndNeverReachesTheEngine()
    {
        var (raw, server, engine) = await StartRawAsync();
        await using var rawGuard = raw;
        await using var serverGuard = server;
        await raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(1, 1, "test", null)).Within();

        var ex = await Assert.ThrowsAsync<BootrixException>(
            () => raw.InvokeAsync<EngineJobResult>(BrokerProtocol.RunJob, new { runId = "x", request = new { type = "format-everything", imagePath = @"C:\a.iso" } }).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        Assert.Equal(0, engine.JobsStarted);
        Assert.False(server.IsClosed);
    }

    [Fact]
    public async Task BrokerAnswersRequestsOnlyFromTheMethodTable()
    {
        // Method names with dots, slashes or odd casing must not resolve to anything.
        var (raw, server, _) = await StartRawAsync();
        await using var rawGuard = raw;
        await using var serverGuard = server;
        await raw.InvokeAsync<HelloResult>(BrokerProtocol.Hello, new HelloParams(1, 1, "test", null)).Within();

        foreach (var method in new[] { "LISTDISKS", "listdisks", "Bootrix.Core.Engine.IEngine.ListDisksAsync", "../listDisks", "listDisks ", "$/ping" })
        {
            await Assert.ThrowsAsync<BootrixException>(() => raw.InvokeAsync<object>(method, new ListDisksParams(new DiskFilter())).Within());
        }
    }

    [Fact]
    public async Task NothingThatPassesThroughTheConnection_EndsUpInTheLog()
    {
        // Requests will carry passwords for local accounts; the log may name methods, ids and sizes, nothing from the payload.
        const string marker = "PAYLOAD-MARKER-7c1f";
        var secret = RandomNumberGenerator.GetBytes(32);
        var logger = new CapturingLogger();
        var engine = new FakeEngine
        {
            Job = (request, progress, _, _) =>
            {
                progress.Report(FakeEngine.Report(1, 2));
                return Task.FromResult(new EngineJobResult { Outcome = JobOutcome.Succeeded });
            },
        };
        await using (var pair = await BrokerPair.CreateAsync(
            engine,
            new BrokerEngineHostOptions { Secret = secret },
            new BrokerClientOptions { Secret = secret },
            logger))
        {
            // Accepted by the validator and the engine.
            await pair.Client.RunJobAsync(TestPaths.ValidRequest($@"C:\images\{marker}.iso"), new SyncProgress(), default).Within();

            // Refused by the validator.
            await pair.Client.RunJobAsync(TestPaths.ValidRequest($@"relative\{marker}.iso"), new SyncProgress(), default).Within();
            await Assert.ThrowsAsync<BootrixException>(() => pair.Client.CaptureIdentityAsync($@"C:\{marker}", default).Within());
        }

        // A method nobody registered, called with a payload.
        var (raw, server, _) = await StartRawAsync(logger: logger);
        await using (raw)
        await using (server)
        {
            await Assert.ThrowsAsync<BootrixException>(() => raw.InvokeAsync<object>("unknownMethod", new { text = marker }).Within());
        }

        // An engine failure that quotes the request in its message.
        var failing = new FakeEngine { Job = (_, _, _, _) => throw new InvalidOperationException($"failed on {marker}") };
        await using (var pair = await BrokerPair.CreateAsync(failing, logger: logger))
        {
            await pair.Client.RunJobAsync(TestPaths.ValidRequest($@"C:\{marker}.iso"), new SyncProgress(), default).Within();
        }

        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, line => line.Contains(marker, StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains(Convert.ToBase64String(secret), StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Lines, line => line.Contains(Convert.ToHexString(secret), StringComparison.OrdinalIgnoreCase));
    }
}
