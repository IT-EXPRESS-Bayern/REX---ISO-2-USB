// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Hosting;
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Tests.Broker;

public sealed class BrokerEntryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-entry-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private BootrixPaths Paths => BootrixPaths.ForPortable(_directory);

    [Theory]
    [InlineData]
    [InlineData("gui", "--other")]
    [InlineData(@"C:\images\ubuntu.iso")]
    [InlineData("--Broker")]
    public void WithoutTheFlag_NothingHappensAndTheNormalStartContinues(params string[] args)
    {
        var handled = BrokerEntry.TryRun(args, out var exitCode, Paths);

        Assert.False(handled);
        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void BrokerFlagWithBadArguments_IsHandledWithExitCode2AndTheReasonInTheBrokerLog()
    {
        var handled = BrokerEntry.TryRun(["--broker", "--pipe", "evil"], out var exitCode, Paths);

        Assert.True(handled);
        Assert.Equal(2, exitCode);
        var log = Assert.Single(Directory.GetFiles(Paths.LogDirectory, "broker-*.log"));
        Assert.Contains("invalid arguments", File.ReadAllText(log), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Paths.LogDirectory, "bootrix-*.log"));
    }

    [Fact]
    public void BrokerFlagAlone_IsHandledAndRefused()
    {
        Assert.True(BrokerEntry.TryRun(["--broker"], out var exitCode, Paths));
        Assert.Equal(2, exitCode);
    }
}
