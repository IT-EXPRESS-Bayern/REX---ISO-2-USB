// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Ipc;

namespace Bootrix.Core.Tests.Engine;

public class DeferredEngineTests
{
    private sealed class RemoteFake : IRemoteEngine, IAsyncDisposable
    {
        private readonly FakeEngine _inner = new();

        public bool IsConnected { get; set; } = true;

        public bool Disposed { get; private set; }

        public Task Disconnected => Task.CompletedTask;

        public event EventHandler? DevicesChanged
        {
            add => _inner.DevicesChanged += value;
            remove => _inner.DevicesChanged -= value;
        }

        public Task<IReadOnlyList<StorageDevice>> ListDisksAsync(DiskFilter filter, CancellationToken cancellationToken) =>
            _inner.ListDisksAsync(filter, cancellationToken);

        public Task<DiskIdentity> CaptureIdentityAsync(string devicePath, CancellationToken cancellationToken) =>
            _inner.CaptureIdentityAsync(devicePath, cancellationToken);

        public Task<EngineJobResult> RunJobAsync(EngineJobRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken, CancellationToken abortToken = default) =>
            _inner.RunJobAsync(request, progress, cancellationToken, abortToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ListingDisks_NeverStartsThePrivilegedEngine()
    {
        var starts = 0;
        var local = new FakeEngine { Devices = [TestPaths.SampleDevice()] };
        await using var engine = new DeferredEngine(local, _ =>
        {
            starts++;
            return Task.FromResult<IEngine>(new FakeEngine());
        });

        var disks = await engine.ListDisksAsync(new DiskFilter(), default);

        Assert.Single(disks);
        Assert.Equal(0, starts);
        Assert.False(engine.PrivilegedRunning);
    }

    [Fact]
    public async Task DevicesChanged_ComesFromTheUnprivilegedEngine()
    {
        var local = new FakeEngine();
        await using var engine = new DeferredEngine(local, _ => Task.FromResult<IEngine>(new FakeEngine()));
        var raised = 0;
        EventHandler handler = (_, _) => raised++;

        engine.DevicesChanged += handler;
        local.RaiseDevicesChanged();
        engine.DevicesChanged -= handler;
        local.RaiseDevicesChanged();

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task FirstPrivilegedCall_StartsTheEngineOnce_EvenWhenCallsRace()
    {
        var starts = 0;
        var privileged = new FakeEngine();
        await using var engine = new DeferredEngine(new FakeEngine(), async token =>
        {
            Interlocked.Increment(ref starts);
            await Task.Delay(100, token);
            return privileged;
        });

        await Task.WhenAll(
            engine.CaptureIdentityAsync(TestPaths.Disk3, default),
            engine.CaptureIdentityAsync(TestPaths.Disk4, default),
            engine.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default));

        Assert.Equal(1, starts);
        Assert.Equal(1, privileged.JobsStarted);
        Assert.True(engine.PrivilegedRunning);
    }

    [Fact]
    public async Task RefusedElevation_IsAFailedJobAndTheNextTryAsksAgain()
    {
        var attempts = 0;
        await using var engine = new DeferredEngine(new FakeEngine(), _ =>
        {
            attempts++;
            return attempts == 1
                ? throw new BootrixException(ErrorCode.ElevationDenied, "1223")
                : Task.FromResult<IEngine>(new FakeEngine());
        });

        var first = await engine.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default);
        var second = await engine.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), default);

        Assert.Equal(JobOutcome.Failed, first.Outcome);
        Assert.Equal(ErrorCode.ElevationDenied, first.ErrorCode);
        Assert.True(second.Succeeded);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RefusedElevation_ThrowsFromTheCallsThatReturnNoResult()
    {
        await using var engine = new DeferredEngine(new FakeEngine(), _ => throw new BootrixException(ErrorCode.ElevationDenied));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => engine.CaptureIdentityAsync(TestPaths.Disk3, default));

        Assert.Equal(ErrorCode.ElevationDenied, ex.Code);
    }

    [Fact]
    public async Task CancellingWhileTheEngineStarts_IsACancelledJob()
    {
        using var cts = new CancellationTokenSource();
        await using var engine = new DeferredEngine(new FakeEngine(), async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new FakeEngine();
        });

        var run = engine.RunJobAsync(TestPaths.ValidRequest(), new SyncProgress(), cts.Token);
        await cts.CancelAsync();

        Assert.Equal(JobOutcome.Canceled, (await run.Within()).Outcome);
    }

    [Fact]
    public async Task EngineThatLostItsConnection_IsReplacedOnTheNextUse()
    {
        var created = new List<RemoteFake>();
        await using var engine = new DeferredEngine(new FakeEngine(), _ =>
        {
            var remote = new RemoteFake();
            created.Add(remote);
            return Task.FromResult<IEngine>(remote);
        });

        await engine.CaptureIdentityAsync(TestPaths.Disk3, default);
        await engine.CaptureIdentityAsync(TestPaths.Disk3, default);
        Assert.Single(created);

        created[0].IsConnected = false;
        Assert.False(engine.PrivilegedRunning);
        await engine.CaptureIdentityAsync(TestPaths.Disk3, default);

        Assert.Equal(2, created.Count);
        Assert.True(created[0].Disposed);
        Assert.False(created[1].Disposed);
    }

    [Fact]
    public async Task Disposing_ReleasesThePrivilegedEngine()
    {
        var remote = new RemoteFake();
        var engine = new DeferredEngine(new FakeEngine(), _ => Task.FromResult<IEngine>(remote));
        await engine.CaptureIdentityAsync(TestPaths.Disk3, default);

        await engine.DisposeAsync();

        Assert.True(remote.Disposed);
    }

    [Fact]
    public async Task Disposing_WithoutEverStartingAnything_IsHarmless()
    {
        var engine = new DeferredEngine(new FakeEngine(), _ => throw new InvalidOperationException("must not start"));

        await engine.DisposeAsync();
    }
}
