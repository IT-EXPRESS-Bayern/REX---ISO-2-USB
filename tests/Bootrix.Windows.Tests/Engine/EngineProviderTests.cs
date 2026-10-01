// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Windows.Engine;
using Bootrix.Windows.Tests.Broker;

namespace Bootrix.Windows.Tests.Engine;

public class EngineProviderTests
{
    private sealed class DisposableFake : IEngine, IAsyncDisposable
    {
        private readonly FakeEngine _inner = new();

        public bool Disposed { get; private set; }

        public event EventHandler? DevicesChanged
        {
            add => _inner.DevicesChanged += value;
            remove => _inner.DevicesChanged -= value;
        }

        public Task<IReadOnlyList<StorageDevice>> ListDisksAsync(DiskFilter filter, CancellationToken cancellationToken) => _inner.ListDisksAsync(filter, cancellationToken);

        public Task<DiskIdentity> CaptureIdentityAsync(string devicePath, CancellationToken cancellationToken) => _inner.CaptureIdentityAsync(devicePath, cancellationToken);

        public Task<EngineJobResult> RunJobAsync(EngineJobRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken, CancellationToken abortToken = default) =>
            _inner.RunJobAsync(request, progress, cancellationToken, abortToken);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ElevatedProcess_UsesTheLocalEngineAndNeverStartsABroker()
    {
        var local = new FakeEngine();
        var starts = 0;
        await using var provider = new EngineProvider(local, _ =>
        {
            starts++;
            return Task.FromResult<IEngine>(new FakeEngine());
        }, elevated: true);

        await provider.Engine.CaptureIdentityAsync(TestDisks.Disk3, default);
        await provider.Engine.RunJobAsync(TestDisks.Request(TestDisks.Device(3, TestDisks.Disk3)), new SyncProgress(), default);

        Assert.True(provider.RunsElevated);
        Assert.Same(local, provider.Engine);
        Assert.Equal(0, starts);
        Assert.Equal(1, local.Identities);
        Assert.Equal(1, local.JobsStarted);
    }

    [Fact]
    public async Task OrdinaryProcess_ListsDisksWithoutStartingTheBroker()
    {
        var local = new FakeEngine { Devices = [TestDisks.Device(3, TestDisks.Disk3)] };
        var starts = 0;
        await using var provider = new EngineProvider(local, _ =>
        {
            starts++;
            return Task.FromResult<IEngine>(new FakeEngine());
        }, elevated: false);

        var disks = await provider.Engine.ListDisksAsync(new DiskFilter(), default);

        Assert.False(provider.RunsElevated);
        Assert.Single(disks);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task OrdinaryProcess_StartsTheBrokerOnTheFirstFingerprintAndKeepsUsingIt()
    {
        var local = new FakeEngine();
        var broker = new FakeEngine();
        var starts = 0;
        await using var provider = new EngineProvider(local, _ =>
        {
            starts++;
            return Task.FromResult<IEngine>(broker);
        }, elevated: false);

        await provider.Engine.CaptureIdentityAsync(TestDisks.Disk3, default);
        await provider.Engine.RunJobAsync(TestDisks.Request(TestDisks.Device(3, TestDisks.Disk3)), new SyncProgress(), default);

        Assert.Equal(1, starts);
        Assert.Equal(1, broker.Identities);
        Assert.Equal(1, broker.JobsStarted);
        Assert.Equal(0, local.Identities);
        Assert.Equal(0, local.JobsStarted);
    }

    [Fact]
    public async Task OrdinaryProcess_DeclinedPrompt_IsAFailedJob()
    {
        await using var provider = new EngineProvider(
            new FakeEngine(),
            _ => throw new BootrixException(ErrorCode.ElevationDenied, "1223"),
            elevated: false);

        var result = await provider.Engine.RunJobAsync(TestDisks.Request(TestDisks.Device(3, TestDisks.Disk3)), new SyncProgress(), default);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.ElevationDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Disposing_EndsTheBroker()
    {
        var broker = new DisposableFake();
        var provider = new EngineProvider(new FakeEngine(), _ => Task.FromResult<IEngine>(broker), elevated: false);
        await provider.Engine.CaptureIdentityAsync(TestDisks.Disk3, default);

        await provider.DisposeAsync();

        Assert.True(broker.Disposed);
    }

    [Fact]
    public async Task Disposing_TwiceIsHarmless()
    {
        var provider = new EngineProvider(new FakeEngine(), _ => Task.FromResult<IEngine>(new FakeEngine()), elevated: false);

        await provider.DisposeAsync();
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ElevatedProcess_DisposingLeavesTheLocalEngineAlone()
    {
        var provider = new EngineProvider(new DisposableFake(), _ => throw new InvalidOperationException(), elevated: true);

        await provider.DisposeAsync();

        Assert.False(((DisposableFake)provider.Engine).Disposed);
    }
}
