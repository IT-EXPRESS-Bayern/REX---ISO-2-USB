// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Storage;

public sealed class DiskWiperTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "bootrix-wipe-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void ClearsHeadAndTailButLeavesTheMiddle()
    {
        const int size = 32 * 1024 * 1024;
        File.WriteAllBytes(_path, Enumerable.Repeat((byte)0xEE, size).ToArray());
        using (var device = new FileBlockDevice(_path, size, 512, create: false))
        {
            DiskWiper.WipeTables(device);
        }

        var data = File.ReadAllBytes(_path);
        Assert.All(data.AsSpan(0, 8 * 1024 * 1024).ToArray(), b => Assert.Equal(0, b));
        Assert.All(data.AsSpan(size - 1024 * 1024).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0xEE, data[8 * 1024 * 1024]);
        Assert.Equal(0xEE, data[size - 1024 * 1024 - 1]);
    }

    [Fact]
    public void SmallDevicesAreWipedWithoutOverrun()
    {
        const int size = 3 * 1024 * 1024;
        File.WriteAllBytes(_path, Enumerable.Repeat((byte)0xEE, size).ToArray());
        using (var device = new FileBlockDevice(_path, size, 4096, create: false))
        {
            DiskWiper.WipeTables(device);
        }

        Assert.All(File.ReadAllBytes(_path), b => Assert.Equal(0, b));
    }

    [Fact]
    public void ZeroRangeClearsOnlyTheRequestedBytes()
    {
        File.WriteAllBytes(_path, Enumerable.Repeat((byte)0xEE, 1024 * 1024).ToArray());
        using (var device = new FileBlockDevice(_path, 1024 * 1024, 512, create: false))
        {
            DiskWiper.ZeroRange(device, 4096, 8192);
        }

        var data = File.ReadAllBytes(_path);
        Assert.Equal(0xEE, data[4095]);
        Assert.Equal(0, data[4096]);
        Assert.Equal(0, data[4096 + 8191]);
        Assert.Equal(0xEE, data[4096 + 8192]);
    }
}
