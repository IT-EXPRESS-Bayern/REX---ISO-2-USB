// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Storage;

public sealed class BlockDeviceStreamTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "bootrix-bds-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void UnalignedWritesAndReadsRoundTrip()
    {
        using var device = new FileBlockDevice(_path, 4 * 1024 * 1024, 4096);
        var data = new byte[300_001];
        new Random(5).NextBytes(data);

        using (var stream = new BlockDeviceStream(device, 4096 * 10, 3 * 1024 * 1024))
        {
            stream.Position = 1234;
            stream.Write(data);
            stream.Flush();

            stream.Position = 1234;
            var back = new byte[data.Length];
            Assert.Equal(data.Length, stream.Read(back));
            Assert.Equal(data, back);
        }

        // Bytes outside the written range stay untouched (read-modify-write of the boundary sectors).
        var raw = File.ReadAllBytes(_path);
        Assert.Equal(data, raw.AsSpan(4096 * 10 + 1234, data.Length).ToArray());
        Assert.All(raw.AsSpan(0, 4096 * 10).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void PreservesNeighbouringBytesInSharedSectors()
    {
        using var device = new FileBlockDevice(_path, 1024 * 1024, 512);
        var existing = Enumerable.Repeat((byte)0xAB, 512).ToArray();
        device.Write(0, existing);

        using (var stream = new BlockDeviceStream(device, 0, 1024 * 1024))
        {
            stream.Position = 100;
            stream.Write([1, 2, 3]);
        }

        var back = new byte[512];
        device.Read(0, back);
        Assert.Equal([0xAB, 0xAB, 0xAB], back.AsSpan(97, 3).ToArray());
        Assert.Equal([1, 2, 3], back.AsSpan(100, 3).ToArray());
        Assert.Equal(0xAB, back[103]);
    }

    [Fact]
    public void WriteBeyondWindowThrows()
    {
        using var device = new FileBlockDevice(_path, 1024 * 1024, 512);
        using var stream = new BlockDeviceStream(device, 0, 4096);
        stream.Position = 4090;

        Assert.Throws<IOException>(() => stream.Write(new byte[10]));
    }

    [Fact]
    public void ReadStopsAtWindowEnd()
    {
        using var device = new FileBlockDevice(_path, 1024 * 1024, 512);
        using var stream = new BlockDeviceStream(device, 512, 1000);
        stream.Position = 900;

        Assert.Equal(100, stream.Read(new byte[500]));
    }

    [Fact]
    public void MisalignedWindowStartIsRejected()
    {
        using var device = new FileBlockDevice(_path, 1024 * 1024, 512);

        Assert.Throws<ArgumentException>(() => new BlockDeviceStream(device, 100, 1000));
    }
}
