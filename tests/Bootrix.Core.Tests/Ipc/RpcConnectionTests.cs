// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Ipc;

namespace Bootrix.Core.Tests.Ipc;

public class RpcConnectionTests
{
    private sealed record Echo(string Text);

    private sealed record Sum(int A, int B);

    private sealed record Number(int Value);

    [Fact]
    public async Task Request_ReturnsTheResultOfTheHandler()
    {
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Sum, Number>("add", (p, _) => Task.FromResult(new Number(p.A + p.B))));

        var result = await pair.Client.InvokeAsync<Number>("add", new Sum(2, 40)).Within();

        Assert.Equal(42, result.Value);
    }

    [Fact]
    public async Task Request_IsAnsweredInTheOtherDirectionToo()
    {
        await using var pair = await RpcPair.CreateAsync(
            clientHandlers: h => h.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p)));

        var result = await pair.Server.InvokeAsync<Echo>("echo", new Echo("from the server")).Within();

        Assert.Equal("from the server", result.Text);
    }

    [Fact]
    public async Task MethodWithoutResult_CompletesTheCall()
    {
        var seen = new TaskCompletionSource<string>();
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo>("remember", (p, _) =>
        {
            seen.SetResult(p.Text);
            return Task.CompletedTask;
        }));

        await pair.Client.InvokeAsync("remember", new Echo("noted")).Within();

        Assert.Equal("noted", await seen.Task.Within());
    }

    [Fact]
    public async Task ParallelRequests_DoNotWaitForEachOther()
    {
        var release = new TaskCompletionSource();
        await using var pair = await RpcPair.CreateAsync(h => h
            .Add<Echo, Echo>("slow", async (p, _) =>
            {
                await release.Task;
                return p;
            })
            .Add<Echo, Echo>("fast", (p, _) => Task.FromResult(p)));

        var slow = pair.Client.InvokeAsync<Echo>("slow", new Echo("slow"));
        var fast = await pair.Client.InvokeAsync<Echo>("fast", new Echo("fast")).Within();

        Assert.Equal("fast", fast.Text);
        Assert.False(slow.IsCompleted);

        release.SetResult();
        Assert.Equal("slow", (await slow.Within()).Text);
    }

    [Fact]
    public async Task ManyParallelRequests_EachGetTheirOwnAnswer()
    {
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Number, Number>("square", async (p, _) =>
        {
            await Task.Yield();
            return new Number(p.Value * p.Value);
        }));

        var calls = Enumerable.Range(0, 300).Select(i => pair.Client.InvokeAsync<Number>("square", new Number(i))).ToArray();
        var results = await Task.WhenAll(calls).Within();

        Assert.Equal(Enumerable.Range(0, 300).Select(i => i * i), results.Select(r => r.Value));
    }

    [Fact]
    public async Task Notifications_ArriveInTheOrderTheyWereSent()
    {
        var received = new List<int>();
        await using var pair = await RpcPair.CreateAsync(h => h
            .AddNotification<Number>("tick", n => received.Add(n.Value))
            .Add<Echo, Echo>("barrier", (p, _) => Task.FromResult(p)));

        for (var i = 0; i < 500; i++)
        {
            await pair.Client.NotifyAsync("tick", new Number(i));
        }

        // Notifications are handled in the read loop before the next frame, so the answer proves they are all in.
        await pair.Client.InvokeAsync<Echo>("barrier", new Echo("done")).Within();

        Assert.Equal(Enumerable.Range(0, 500), received);
    }

    [Fact]
    public async Task UnknownNotification_IsIgnored()
    {
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p)));

        await pair.Client.NotifyAsync("nobody-listens", new Echo("x"));

        Assert.Equal("still alive", (await pair.Client.InvokeAsync<Echo>("echo", new Echo("still alive")).Within()).Text);
    }

    [Fact]
    public async Task UnknownMethod_FailsWithAProtocolErrorAndTheConnectionSurvives()
    {
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p)));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => pair.Client.InvokeAsync<Echo>("format-c", new Echo("x")).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        Assert.Equal("still alive", (await pair.Client.InvokeAsync<Echo>("echo", new Echo("still alive")).Within()).Text);
    }

    [Fact]
    public async Task BootrixException_ArrivesWithCodeArgumentsAndDetail()
    {
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("fail", (_, _) =>
            throw new BootrixException(ErrorCode.DeviceTooSmall, "technical text") { Arguments = ["8 GB", 4] }));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => pair.Client.InvokeAsync<Echo>("fail", new Echo("x")).Within());

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
        Assert.Equal("technical text", ex.Detail);
        Assert.Equal(["8 GB", "4"], ex.Arguments);
        Assert.Contains("8 GB", ErrorCatalog.Describe(ex).Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherExceptions_ArriveAsUnknownWithoutLeavingTheConnectionBroken()
    {
        await using var pair = await RpcPair.CreateAsync(h => h
            .Add<Echo, Echo>("crash", (_, _) => throw new InvalidOperationException("kaputt"))
            .Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p)));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => pair.Client.InvokeAsync<Echo>("crash", new Echo("x")).Within());

        Assert.Equal(ErrorCode.Unknown, ex.Code);
        Assert.Contains("kaputt", ex.Detail, StringComparison.Ordinal);
        Assert.Equal("ok", (await pair.Client.InvokeAsync<Echo>("echo", new Echo("ok")).Within()).Text);
    }

    [Fact]
    public async Task ParametersOfTheWrongShape_AreRefusedAsProtocolErrors()
    {
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Sum, Number>("add", (p, _) => Task.FromResult(new Number(p.A + p.B))));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => pair.Client.InvokeAsync<Number>("add", "just a string").Within());
        var missing = await Assert.ThrowsAsync<BootrixException>(() => pair.Client.InvokeAsync<Number>("add", null).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        Assert.Equal(ErrorCode.BrokerProtocol, missing.Code);
        Assert.False(pair.Client.IsClosed);
    }

    [Fact]
    public async Task Cancel_EndsTheCallAtOnceAndStopsTheRemoteHandler()
    {
        var started = new TaskCompletionSource();
        var stopped = new TaskCompletionSource<bool>();
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("wait", async (p, context) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
                return p;
            }
            catch (OperationCanceledException)
            {
                stopped.SetResult(true);
                throw;
            }
        }));

        using var cts = new CancellationTokenSource();
        var call = pair.Client.InvokeAsync<Echo>("wait", new Echo("x"), cts.Token);
        await started.Task.Within();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.Within());
        Assert.True(await stopped.Task.Within());
    }

    [Fact]
    public async Task Cancel_BeforeTheCall_ThrowsWithoutContactingThePeer()
    {
        var called = false;
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("echo", (p, _) =>
        {
            called = true;
            return Task.FromResult(p);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pair.Client.InvokeAsync<Echo>("echo", new Echo("x"), new CancellationToken(true)));
        await pair.Client.NotifyAsync("flush");

        Assert.False(called);
    }

    [Fact]
    public async Task CooperativeCall_SoftCancelReachesTheHandlerButTheCallEndsWithItsAnswer()
    {
        var started = new TaskCompletionSource();
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("job", async (_, context) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cleanup takes a moment before the answer is given.
                await Task.Delay(50);
            }

            return new Echo(context.AbortToken.IsCancellationRequested ? "aborted" : "stopped softly");
        }));

        using var soft = new CancellationTokenSource();
        var call = pair.Client.InvokeCooperativeAsync<Echo>("job", new Echo("x"), soft.Token, CancellationToken.None);
        await started.Task.Within();
        await soft.CancelAsync();

        Assert.Equal("stopped softly", (await call.Within()).Text);
    }

    [Fact]
    public async Task CooperativeCall_AbortSetsBothTokensOfTheHandler()
    {
        var started = new TaskCompletionSource();
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("job", async (_, context) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, context.AbortToken);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            return new Echo($"soft={context.CancellationToken.IsCancellationRequested} abort={context.AbortToken.IsCancellationRequested}");
        }));

        using var abort = new CancellationTokenSource();
        var call = pair.Client.InvokeCooperativeAsync<Echo>("job", new Echo("x"), CancellationToken.None, abort.Token);
        await started.Task.Within();
        await abort.CancelAsync();

        Assert.Equal("soft=True abort=True", (await call.Within()).Text);
    }

    [Fact]
    public async Task SoftCancel_DoesNotSetTheAbortToken()
    {
        var started = new TaskCompletionSource();
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("job", async (_, context) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, context.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            return new Echo($"abort={context.AbortToken.IsCancellationRequested}");
        }));

        using var soft = new CancellationTokenSource();
        var call = pair.Client.InvokeCooperativeAsync<Echo>("job", new Echo("x"), soft.Token, CancellationToken.None);
        await started.Task.Within();
        await soft.CancelAsync();

        Assert.Equal("abort=False", (await call.Within()).Text);
    }

    [Fact]
    public async Task PeerDisappearsMidCall_TheCallFailsWithTheClosedException()
    {
        var started = new TaskCompletionSource();
        var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("hang", async (p, context) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, context.AbortToken);
            return p;
        }));

        var call = pair.Client.InvokeAsync<Echo>("hang", new Echo("x"));
        await started.Task.Within();
        await pair.Server.DisposeAsync();

        var ex = await Assert.ThrowsAsync<RpcConnectionClosedException>(() => call.Within());
        Assert.Equal(ErrorCode.BrokerDisconnected, ex.Code);
        await pair.Client.Completion.Within();
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task CallsAfterTheConnectionEnded_FailImmediately()
    {
        var pair = await RpcPair.CreateAsync();
        await pair.Server.DisposeAsync();
        await pair.Client.Completion.Within();

        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => pair.Client.InvokeAsync<Echo>("echo", new Echo("x")));
        await Assert.ThrowsAsync<RpcConnectionClosedException>(() => pair.Client.NotifyAsync("tick"));
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task LosingTheCaller_AbortsTheRunningHandlerAndWaitsForItsCleanup()
    {
        var started = new TaskCompletionSource();
        var cleanedUp = false;
        var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("job", async (p, context) =>
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
        }));

        _ = pair.Client.InvokeAsync<Echo>("job", new Echo("x"));
        await started.Task.Within();
        await pair.Client.DisposeAsync();

        await pair.Server.Completion.Within();
        Assert.True(cleanedUp);
        await pair.DisposeAsync();
    }

    [Fact]
    public async Task ResponseThatDoesNotFitAFrame_ReachesTheCallerAsAnErrorAndTheConnectionStaysUsable()
    {
        var options = new RpcConnectionOptions { MaxFrameBytes = 2048 };
        await using var pair = await RpcPair.CreateAsync(
            h => h
                .Add<Number, Echo>("big", (p, _) => Task.FromResult(new Echo(new string('x', p.Value))))
                .Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p)),
            options: options);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => pair.Client.InvokeAsync<Echo>("big", new Number(10_000)).Within());

        Assert.Equal(ErrorCode.BrokerProtocol, ex.Code);
        Assert.Equal(100, (await pair.Client.InvokeAsync<Echo>("big", new Number(100)).Within()).Text.Length);
    }

    [Fact]
    public async Task RequestThatDoesNotFitAFrame_ThrowsOnTheSendingSide()
    {
        var options = new RpcConnectionOptions { MaxFrameBytes = 2048 };
        await using var pair = await RpcPair.CreateAsync(h => h.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p)), options: options);

        await Assert.ThrowsAsync<RpcProtocolException>(() => pair.Client.InvokeAsync<Echo>("echo", new Echo(new string('x', 10_000))).Within());

        Assert.Equal("small", (await pair.Client.InvokeAsync<Echo>("echo", new Echo("small")).Within()).Text);
    }

    [Fact]
    public void ReservedMethodNames_CannotBeRegistered()
    {
        Assert.Throws<ArgumentException>(() => new RpcHandlerTable().Add<Echo, Echo>("$/cancel", (p, _) => Task.FromResult(p)));
    }

    [Fact]
    public void DuplicateMethodNames_AreRefused()
    {
        var table = new RpcHandlerTable().Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p));

        Assert.Throws<ArgumentException>(() => table.Add<Echo, Echo>("echo", (p, _) => Task.FromResult(p)));
    }

    [Fact]
    public async Task HandlersCannotBeAddedOnceTheConnectionRuns()
    {
        await using var pair = await RpcPair.CreateAsync();

        Assert.Throws<InvalidOperationException>(() => pair.Server.Handlers.Add<Echo, Echo>("late", (p, _) => Task.FromResult(p)));
    }
}
