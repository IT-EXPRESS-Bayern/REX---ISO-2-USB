// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using Bootrix.Core.Storage.Testing;

namespace Bootrix.Core.Tests.Storage.Testing;

public sealed class BadBlockTesterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "bootrix-bbt-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task HealthyDeviceIsClean()
    {
        using var device = new FileBlockDevice(_path, 8L << 20, 512);

        var result = await BadBlockTester.RunAsync(device);

        Assert.True(result.IsClean);
        Assert.Equal(4, result.Passes);
        Assert.Equal(8L << 20, result.BytesTested);
    }

    [Fact]
    public async Task BadSectorsAreLocatedAndMerged()
    {
        using var inner = new FileBlockDevice(_path, 8L << 20, 512);
        var flaky = new FlakyDevice(inner, 100, 101, 102, 5000);

        var result = await BadBlockTester.RunAsync(flaky, BadBlockTester.QuickPatterns, chunkBytes: 64 * 1024);

        Assert.False(result.IsClean);
        Assert.Equal(2, result.BadRanges.Count);
        Assert.Equal(new BadRange(100 * 512, 3 * 512), result.BadRanges[0]);
        Assert.Equal(new BadRange(5000 * 512, 512), result.BadRanges[1]);
    }

    [Fact]
    public async Task ProgressReachesOne()
    {
        using var device = new FileBlockDevice(_path, 2L << 20, 512);
        double last = 0;

        await BadBlockTester.RunAsync(device, BadBlockTester.QuickPatterns, progress: new SyncProgress(v => last = v));

        Assert.Equal(1.0, last, 3);
    }

    private sealed class SyncProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
