// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using Bootrix.Core.Storage.Testing;

namespace Bootrix.Core.Tests.Storage.Testing;

public sealed class CapacityProbeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "bootrix-probe-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task GenuineDevicePasses()
    {
        using var device = new FileBlockDevice(_path, 128L << 20, 512);

        var result = await CapacityProbe.RunAsync(device);

        Assert.True(result.IsGenuine);
        Assert.Equal(0, result.FailedPoints);
        Assert.Equal(128L << 20, result.EstimatedRealBytes);
    }

    [Fact]
    public async Task WraparoundDeviceIsDetectedAndSizeEstimated()
    {
        using var real = new FileBlockDevice(_path, 32L << 20, 512);
        var fake = new WraparoundDevice(real, 512L << 20);

        var result = await CapacityProbe.RunAsync(fake);

        Assert.False(result.IsGenuine);
        Assert.True(result.FailedPoints > 0);
        Assert.Equal(32L << 20, result.EstimatedRealBytes);
    }

    [Fact]
    public async Task OddRealSizeIsEstimatedToItsGranularity()
    {
        using var real = new FileBlockDevice(_path, 29L << 20, 512);
        var fake = new WraparoundDevice(real, 400L << 20);

        var result = await CapacityProbe.RunAsync(fake);

        Assert.False(result.IsGenuine);
        Assert.NotNull(result.EstimatedRealBytes);
        Assert.Equal(0, 29L * (1 << 20) % result.EstimatedRealBytes!.Value);
    }

    [Fact]
    public async Task SwallowingDeviceIsDetected()
    {
        using var real = new FileBlockDevice(_path, 32L << 20, 512);
        var fake = new SwallowingDevice(real, 256L << 20);

        var result = await CapacityProbe.RunAsync(fake);

        Assert.False(result.IsGenuine);
        Assert.NotNull(result.FirstFailingOffset);
        Assert.True(result.FirstFailingOffset >= (32L << 20) - 4096);
    }

    [Fact]
    public async Task CancellationIsHonoured()
    {
        using var device = new FileBlockDevice(_path, 64L << 20, 512);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CapacityProbe.RunAsync(device, cancellationToken: cts.Token));
    }

    [Fact]
    public void ProbePointsAreReproducibleAndIncludeBothEnds()
    {
        var a = CapacityProbe.PickOffsets(100_000, 500, 7).ToList();
        var b = CapacityProbe.PickOffsets(100_000, 500, 7).ToList();

        Assert.Equal(a, b);
        Assert.Equal(500, a.Count);
        Assert.Contains(0L, a);
        Assert.Contains(99_999L, a);
    }
}
