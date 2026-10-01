// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Storage;

/// <summary>Block-map writes, the source observer and the limits that keep a decompression bomb from running on.</summary>
public sealed class RawImageWriterSparseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-sparse-" + Guid.NewGuid().ToString("N"));

    public RawImageWriterSparseTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static RawWriteOptions Options(SparseWriteMap? sparse = null, SourceObserver? observer = null) => new()
    {
        ChunkSize = 64 * 1024,
        HoldBackBytes = 128 * 1024,
        BufferCount = 3,
        Sparse = sparse,
        SourceObserver = observer,
    };

    private FileBlockDevice Device(string name, long length, byte fill)
    {
        var device = new FileBlockDevice(Path.Combine(_dir, name), length);
        if (fill != 0)
        {
            var block = Enumerable.Repeat(fill, 64 * 1024).ToArray();
            for (long offset = 0; offset < length; offset += block.Length)
            {
                device.Write(offset, block.AsSpan(0, (int)Math.Min(block.Length, length - offset)));
            }
        }

        return device;
    }

    private static byte[] Image(int length)
    {
        var data = new byte[length];
        new Random(5).NextBytes(data);
        return data;
    }

    private static byte[] ReadBack(FileBlockDevice device, int length)
    {
        var buffer = new byte[(length + 511) / 512 * 512];
        device.Read(0, buffer);
        return buffer[..length];
    }

    [Fact]
    public async Task OnlyMappedRangesAreWritten_TheGapsKeepWhatTheTargetHeld()
    {
        var image = Image(1_000_000);
        using var device = Device("a.img", 2 << 20, fill: 0xAA);
        var map = new SparseWriteMap([new ByteRange(0, 8192), new ByteRange(300_000, 100_000), new ByteRange(900_000, 100_000)]);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [device], Options(map));

        Assert.True(report.AllSucceeded);
        Assert.True(report.Targets[0].Verified);
        var back = ReadBack(device, image.Length);
        Assert.Equal(image[..8192], back[..8192]);
        Assert.All(back[8192..299_520], b => Assert.Equal(0xAA, b));
        Assert.Equal(image[300_000..400_000], back[300_000..400_000]);
        Assert.Equal(image[900_000..], back[900_000..]);
        Assert.True(report.SkippedBytes > 600_000);
        Assert.True(report.Targets[0].BytesWritten < 400_000);
    }

    [Fact]
    public async Task FillGaps_WritesTheWholeImageWithZerosWhereTheMapHasNoData()
    {
        var image = Image(700_000);
        using var device = Device("b.img", 2 << 20, fill: 0xAA);
        var map = new SparseWriteMap([new ByteRange(4096, 8192), new ByteRange(500_000, 4096)], fillGaps: true);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [device], Options(map));

        Assert.True(report.AllSucceeded && report.Targets[0].Verified);
        var back = ReadBack(device, image.Length);
        Assert.All(back[..4096], b => Assert.Equal(0, b));
        Assert.Equal(image[4096..12288], back[4096..12288]);
        Assert.All(back[12288..500_000], b => Assert.Equal(0, b));
        Assert.Equal(image[500_000..504_096], back[500_000..504_096]);
        Assert.All(back[504_096..], b => Assert.Equal(0, b));
        Assert.Equal(0, report.SkippedBytes);
    }

    [Fact]
    public async Task EmptyMap_WritesNothingAndStillSucceeds()
    {
        var image = Image(300_000);
        using var inner = Device("c.img", 1 << 20, fill: 0xAA);
        var recorder = new CountingDevice(inner);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [recorder], Options(new SparseWriteMap([])));

        Assert.True(report.AllSucceeded);
        Assert.Equal(0, recorder.Writes);
        Assert.Equal(image.Length, report.SkippedBytes);
    }

    [Fact]
    public async Task SparseWrite_ToSeveralTargets_WritesTheSameRanges()
    {
        var image = Image(900_000);
        using var a = Device("d1.img", 2 << 20, fill: 0x11);
        using var b = Device("d2.img", 2 << 20, fill: 0x11);
        var map = new SparseWriteMap([new ByteRange(131_072, 65_536), new ByteRange(800_000, 100_000)]);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [a, b], Options(map));

        Assert.All(report.Targets, t => Assert.True(t.Succeeded && t.Verified));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir, "d1.img")), File.ReadAllBytes(Path.Combine(_dir, "d2.img")));
    }

    [Fact]
    public async Task ReadBackFailure_InAMappedRange_IsStillDetected()
    {
        var image = Image(500_000);
        using var inner = Device("e.img", 1 << 20, fill: 0);
        var device = new CorruptingDevice(inner, offset: 200_100);
        var map = new SparseWriteMap([new ByteRange(200_000, 10_000)]);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [device], Options(map));

        var error = Assert.IsType<BootrixException>(report.Targets[0].Error);
        Assert.Equal(ErrorCode.VerifyMismatch, error.Code);
    }

    [Fact]
    public async Task SourceObserver_SeesTheImageInOrderWithoutGaps()
    {
        var image = Image(400_000);
        using var device = Device("f.img", 1 << 20, fill: 0);
        var seen = new MemoryStream();
        var position = 0L;

        await new RawImageWriter().WriteAsync(
            new MemoryStream(image),
            image.Length,
            [device],
            Options(observer: (offset, data) =>
            {
                Assert.Equal(position, offset);
                position += data.Length;
                seen.Write(data);
            }));

        Assert.Equal(image, seen.ToArray());
    }

    [Fact]
    public async Task ObserverException_AbortsTheWrite()
    {
        var image = Image(400_000);
        using var device = Device("g.img", 1 << 20, fill: 0);

        var error = await Assert.ThrowsAsync<BootrixException>(() => new RawImageWriter().WriteAsync(
            new MemoryStream(image),
            image.Length,
            [device],
            Options(observer: (offset, _) =>
            {
                if (offset > 100_000)
                {
                    throw new BootrixException(ErrorCode.ImageHashMismatch, "test");
                }
            })));

        Assert.Equal(ErrorCode.ImageHashMismatch, error.Code);
    }

    [Fact]
    public async Task StreamLongerThanTheTarget_WithUnknownLength_FailsAsTooLargeAndStopsReading()
    {
        using var device = Device("h.img", 1 << 20, fill: 0);
        var endless = new EndlessStream();

        var report = await new RawImageWriter().WriteAsync(endless, null, [device], Options());

        var error = Assert.IsType<BootrixException>(report.Targets[0].Error);
        Assert.Equal(ErrorCode.ImageTooLarge, error.Code);
        Assert.InRange(endless.BytesServed, 1 << 20, 4 << 20);
    }

    [Fact]
    public async Task StreamLongerThanTheTarget_FailsOnlyTheSmallTarget()
    {
        var image = Image(3 << 20);
        using var small = Device("small.img", 1 << 20, fill: 0);
        using var big = Device("big.img", 4 << 20, fill: 0);

        var report = await new RawImageWriter().WriteAsync(new NoLengthStream(image), null, [small, big], Options());

        Assert.False(report.Targets[0].Succeeded);
        Assert.Equal(ErrorCode.ImageTooLarge, ((BootrixException)report.Targets[0].Error!).Code);
        Assert.True(report.Targets[1].Succeeded && report.Targets[1].Verified);
        Assert.Equal(image, ReadBack(big, image.Length));
    }

    [Fact]
    public async Task StreamLongerThanItsDeclaredLength_IsRejected()
    {
        var image = Image(500_000);
        using var device = Device("i.img", 2 << 20, fill: 0);

        var error = await Assert.ThrowsAsync<BootrixException>(() =>
            new RawImageWriter().WriteAsync(new MemoryStream(image), 400_000, [device], Options()));

        Assert.Equal(ErrorCode.ImageCorrupt, error.Code);
    }

    [Fact]
    public async Task StreamShorterThanItsDeclaredLength_IsReportedAsTruncated()
    {
        var image = Image(300_000);
        using var device = Device("j.img", 2 << 20, fill: 0);

        var error = await Assert.ThrowsAsync<BootrixException>(() =>
            new RawImageWriter().WriteAsync(new MemoryStream(image), 400_000, [device], Options()));

        Assert.Equal(ErrorCode.ImageTruncated, error.Code);
    }

    private sealed class EndlessStream : Stream
    {
        public long BytesServed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)0x5A, offset, count);
            BytesServed += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NoLengthStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingDevice(IBlockDevice inner) : IBlockDevice
    {
        private int _writes;

        public int Writes => _writes;

        public string Name => inner.Name;

        public int SectorSize => inner.SectorSize;

        public long Length => inner.Length;

        public int BufferAlignment => inner.BufferAlignment;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            Interlocked.Increment(ref _writes);
            inner.Write(offset, data);
        }

        public int Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

        public void Flush() => inner.Flush();

        public void Dispose()
        {
        }
    }

    private sealed class CorruptingDevice(IBlockDevice inner, long offset) : IBlockDevice
    {
        public string Name => inner.Name;

        public int SectorSize => inner.SectorSize;

        public long Length => inner.Length;

        public int BufferAlignment => inner.BufferAlignment;

        public void Write(long at, ReadOnlySpan<byte> data) => inner.Write(at, data);

        public int Read(long at, Span<byte> buffer)
        {
            var read = inner.Read(at, buffer);
            if (offset >= at && offset < at + buffer.Length)
            {
                buffer[(int)(offset - at)] ^= 0xFF;
            }

            return read;
        }

        public void Flush() => inner.Flush();

        public void Dispose()
        {
        }
    }
}
