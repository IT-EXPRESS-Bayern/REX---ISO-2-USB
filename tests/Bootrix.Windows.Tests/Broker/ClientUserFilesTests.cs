// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Tests.Broker;

public sealed class ClientUserFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootrix-userfiles-" + Guid.NewGuid().ToString("N")[..10]);

    public ClientUserFilesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class CountingImpersonator : IClientImpersonator
    {
        public int Scopes { get; private set; }

        public async Task<T> RunAsClientAsync<T>(Func<Task<T>> action)
        {
            Scopes++;
            return await action().ConfigureAwait(false);
        }
    }

    private sealed class RefusingImpersonator : IClientImpersonator
    {
        public Task<T> RunAsClientAsync<T>(Func<Task<T>> action) =>
            throw new BootrixException(ErrorCode.BrokerDisconnected, "no client is connected");
    }

    private string PathOf(string name) => Path.Combine(_root, name);

    private byte[] WriteBytes(string name, int length)
    {
        var bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        File.WriteAllBytes(PathOf(name), bytes);
        return bytes;
    }

    [Fact]
    public async Task CopyIn_CopiesTheFileAndReportsItsProgress()
    {
        var content = WriteBytes("source.iso", 3_000_000);
        var impersonator = new CountingImpersonator();
        var reported = new List<(long Done, long Total)>();

        await new ClientUserFiles(impersonator).CopyInAsync(PathOf("source.iso"), PathOf("local.iso"), (done, total) => reported.Add((done, total)), default);

        Assert.Equal(content, File.ReadAllBytes(PathOf("local.iso")));
        Assert.Equal(1, impersonator.Scopes);
        Assert.Equal((3_000_000L, 3_000_000L), reported[^1]);
    }

    [Fact]
    public async Task CopyIn_OfAMissingFile_IsAnUnreadableImage()
    {
        var error = await Assert.ThrowsAsync<BootrixException>(() =>
            new ClientUserFiles(new CountingImpersonator()).CopyInAsync(PathOf("missing.iso"), PathOf("local.iso"), (_, _) => { }, default));

        Assert.Equal(ErrorCode.ImageUnreadable, error.Code);
        Assert.False(File.Exists(PathOf("local.iso")));
    }

    [Fact]
    public async Task CopyIn_WithoutAClient_OpensNothing()
    {
        WriteBytes("source.iso", 10);

        await Assert.ThrowsAsync<BootrixException>(() =>
            new ClientUserFiles(new RefusingImpersonator()).CopyInAsync(PathOf("source.iso"), PathOf("local.iso"), (_, _) => { }, default));

        Assert.False(File.Exists(PathOf("local.iso")));
    }

    [Fact]
    public async Task CopyOut_PlacesTheFileAndLeavesNoPartialFileBehind()
    {
        var content = WriteBytes("local.iso", 2_500_000);
        var impersonator = new CountingImpersonator();

        await new ClientUserFiles(impersonator).CopyOutAsync(PathOf("local.iso"), PathOf("result.iso"), (_, _) => { }, default);

        Assert.Equal(content, File.ReadAllBytes(PathOf("result.iso")));
        Assert.False(File.Exists(PathOf("result.iso.bootrix-part")));
        Assert.Equal(2, impersonator.Scopes);
    }

    [Fact]
    public async Task CopyOut_ReplacesAnExistingFile()
    {
        var content = WriteBytes("local.iso", 1000);
        File.WriteAllText(PathOf("result.iso"), "old");

        await new ClientUserFiles(new CountingImpersonator()).CopyOutAsync(PathOf("local.iso"), PathOf("result.iso"), (_, _) => { }, default);

        Assert.Equal(content, File.ReadAllBytes(PathOf("result.iso")));
    }

    [Fact]
    public async Task CopyOut_Cancelled_RemovesThePartialFileAndKeepsTheOldResult()
    {
        WriteBytes("local.iso", 5_000_000);
        File.WriteAllText(PathOf("result.iso"), "old");
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClientUserFiles(new CountingImpersonator()).CopyOutAsync(PathOf("local.iso"), PathOf("result.iso"), (done, _) => cancel.Cancel(), cancel.Token));

        Assert.False(File.Exists(PathOf("result.iso.bootrix-part")));
        Assert.Equal("old", File.ReadAllText(PathOf("result.iso")));
    }

    [Fact]
    public async Task CopyOut_ToAMissingFolder_IsACopyFailureNamingThePath()
    {
        WriteBytes("local.iso", 10);
        var target = PathOf(Path.Combine("nowhere", "result.iso"));

        var error = await Assert.ThrowsAsync<BootrixException>(() =>
            new ClientUserFiles(new CountingImpersonator()).CopyOutAsync(PathOf("local.iso"), target, (_, _) => { }, default));

        Assert.Equal(ErrorCode.FileCopyFailed, error.Code);
        Assert.Equal(target, error.Arguments[0]);
    }
}
