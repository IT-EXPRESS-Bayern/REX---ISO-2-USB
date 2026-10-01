// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Windows.Broker;
using Bootrix.Windows.Jobs;

namespace Bootrix.Windows.Tests.Broker;

public class ImpersonatingImageStreamProviderTests
{
    private sealed class ScopeTrackingImpersonator : IClientImpersonator
    {
        private static readonly AsyncLocal<bool> Active = new();

        public static bool IsImpersonating => Active.Value;

        public int Scopes { get; private set; }

        public async Task<T> RunAsClientAsync<T>(Func<Task<T>> action)
        {
            Scopes++;
            Active.Value = true;
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                Active.Value = false;
            }
        }
    }

    private sealed class RefusingImpersonator : IClientImpersonator
    {
        public Task<T> RunAsClientAsync<T>(Func<Task<T>> action) =>
            throw new BootrixException(ErrorCode.BrokerDisconnected, "no client is connected");
    }

    private sealed class RecordingProvider(Func<string, Task<OpenedImage>>? open = null) : IImageStreamProvider
    {
        public List<(string Path, bool Impersonating)> Opened { get; } = [];

        public async Task<OpenedImage> OpenAsync(string path, CancellationToken cancellationToken)
        {
            Opened.Add((path, ScopeTrackingImpersonator.IsImpersonating));
            return open is null ? new OpenedImage(new MemoryStream([1, 2, 3]), 3) : await open(path).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TheImageIsOpenedInsideTheClientsScope()
    {
        var inner = new RecordingProvider();
        var impersonator = new ScopeTrackingImpersonator();
        var provider = new ImpersonatingImageStreamProvider(inner, impersonator);

        await using var image = await provider.OpenAsync(@"C:\images\a.iso", default);

        var opened = Assert.Single(inner.Opened);
        Assert.Equal(@"C:\images\a.iso", opened.Path);
        Assert.True(opened.Impersonating);
        Assert.Equal(1, impersonator.Scopes);
        Assert.Equal(3, image.Length);
    }

    [Fact]
    public async Task AnOpenThatContinuesAfterAnAwait_StaysInsideTheScope()
    {
        var impersonatingAfterAwait = false;
        var inner = new RecordingProvider(async _ =>
        {
            await Task.Delay(20);
            impersonatingAfterAwait = ScopeTrackingImpersonator.IsImpersonating;
            return new OpenedImage(new MemoryStream(), 0);
        });
        var provider = new ImpersonatingImageStreamProvider(inner, new ScopeTrackingImpersonator());

        await using var image = await provider.OpenAsync(@"C:\images\a.iso", default);

        Assert.True(impersonatingAfterAwait);
    }

    [Fact]
    public async Task WithoutAClientToImpersonate_NothingIsOpenedAtAll()
    {
        var inner = new RecordingProvider();
        var provider = new ImpersonatingImageStreamProvider(inner, new RefusingImpersonator());

        var ex = await Assert.ThrowsAsync<BootrixException>(() => provider.OpenAsync(@"C:\Windows\System32\config\SAM", default));

        Assert.Equal(ErrorCode.BrokerDisconnected, ex.Code);
        Assert.Empty(inner.Opened);
    }

    [Fact]
    public async Task ErrorsOfTheOpen_ReachTheCaller()
    {
        var inner = new RecordingProvider(_ => throw new UnauthorizedAccessException("the user may not read this"));
        var provider = new ImpersonatingImageStreamProvider(inner, new ScopeTrackingImpersonator());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.OpenAsync(@"C:\secret.iso", default));
    }

    [Fact]
    public async Task EveryOpenGetsItsOwnScope()
    {
        var inner = new RecordingProvider();
        var impersonator = new ScopeTrackingImpersonator();
        var provider = new ImpersonatingImageStreamProvider(inner, impersonator);

        await using var first = await provider.OpenAsync(@"C:\a.iso", default);
        await using var second = await provider.OpenAsync(@"C:\b.iso", default);

        Assert.Equal(2, impersonator.Scopes);
        Assert.All(inner.Opened, o => Assert.True(o.Impersonating));
        Assert.False(ScopeTrackingImpersonator.IsImpersonating);
    }
}
