// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Broker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Broker;

public class ParentProcessWatcherTests
{
    private const string Own = @"C:\Program Files\Bootrix\Bootrix.exe";

    [Fact]
    public async Task ProcessRunningAnotherProgram_CountsAsGone()
    {
        // A reused process id must not keep the broker alive: whatever runs under it now is not our GUI.
        await ParentProcessWatcher.WaitForExitAsync(Environment.ProcessId, Own, _ => @"C:\Windows\notepad.exe", NullLogger.Instance, CancellationToken.None).Within();
    }

    [Fact]
    public async Task ProcessWhoseImageIsUnknown_CountsAsGone()
    {
        await ParentProcessWatcher.WaitForExitAsync(Environment.ProcessId, Own, _ => null, NullLogger.Instance, CancellationToken.None).Within();
    }

    [Fact]
    public async Task ProcessThatDoesNotExist_CountsAsGone()
    {
        await ParentProcessWatcher.WaitForExitAsync(int.MaxValue - 7, Own, _ => Own, NullLogger.Instance, CancellationToken.None).Within();
    }

    [Fact]
    public async Task RunningParent_KeepsTheWaitOpenUntilItIsCancelled()
    {
        using var cts = new CancellationTokenSource();

        var wait = ParentProcessWatcher.WaitForExitAsync(Environment.ProcessId, Own, _ => Own, NullLogger.Instance, cts.Token);
        await Task.Delay(200);
        Assert.False(wait.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.Within());
    }
}
