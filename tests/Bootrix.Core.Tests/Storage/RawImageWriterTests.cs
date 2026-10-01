// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Storage;

public sealed class RawImageWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-raw-" + Guid.NewGuid().ToString("N"));

    public RawImageWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly string[] TargetNames = ["t1.img", "t2.img", "t3.img"];

    private static byte[] RandomImage(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private FileBlockDevice Device(string name, long length, int sector = 512) =>
        new(Path.Combine(_dir, name), length, sector);

    private static RawWriteOptions SmallChunks() => new() { ChunkSize = 64 * 1024, HoldBackBytes = 128 * 1024, BufferCount = 3 };

    [Fact]
    public async Task WritesImageWithPaddingAndMatchingHash()
    {
        var image = RandomImage(1_000_003);
        using var device = Device("a.img", 4 * 1024 * 1024);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [device], SmallChunks());

        Assert.True(report.AllSucceeded);
        Assert.Equal(image.Length, report.ImageBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(image)), report.Sha256);
        Assert.True(report.Targets[0].Verified);

        var written = new byte[image.Length + 512];
        device.Read(0, written.AsSpan(0, (image.Length + 511) / 512 * 512));
        Assert.Equal(image, written.AsSpan(0, image.Length).ToArray());
        Assert.All(written.AsSpan(image.Length, (image.Length + 511) / 512 * 512 - image.Length).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task HeadIsWrittenLast()
    {
        var image = RandomImage(600_000);
        using var inner = Device("order.img", 2 * 1024 * 1024);
        var recorder = new RecordingDevice(inner);

        await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [recorder], SmallChunks());

        Assert.NotEqual(0, recorder.Offsets[0]);
        Assert.Equal(0, recorder.Offsets[^1]);
    }

    [Fact]
    public async Task WritesIdenticalDataToAllTargets()
    {
        var image = RandomImage(2_345_678, seed: 7);
        using var a = Device("t1.img", 4 << 20);
        using var b = Device("t2.img", 4 << 20);
        using var c = Device("t3.img", 4 << 20);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [a, b, c], SmallChunks());

        Assert.All(report.Targets, t => Assert.True(t.Succeeded && t.Verified));
        var files = TargetNames.Select(n => File.ReadAllBytes(Path.Combine(_dir, n))).ToList();
        Assert.Equal(files[0], files[1]);
        Assert.Equal(files[0], files[2]);
    }

    [Fact]
    public async Task FailingTargetDoesNotStopTheOthers()
    {
        var image = RandomImage(3_000_000);
        using var good = Device("good.img", 4 << 20);
        using var badInner = Device("bad.img", 4 << 20);
        var bad = new RecordingDevice(badInner) { FailOnWriteNumber = 5 };

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [bad, good], SmallChunks());

        Assert.False(report.AllSucceeded);
        Assert.False(report.Targets[0].Succeeded);
        Assert.IsType<IOException>(report.Targets[0].Error);
        Assert.True(report.Targets[1].Succeeded);
        Assert.True(report.Targets[1].Verified);
    }

    [Fact]
    public async Task TooSmallTargetIsRejectedBeforeWriting()
    {
        var image = RandomImage(2_000_000);
        using var small = Device("small.img", 1 << 20);
        var recorder = new RecordingDevice(small);
        using var big = Device("big.img", 4 << 20);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [recorder, big], SmallChunks());

        var error = Assert.IsType<BootrixException>(report.Targets[0].Error);
        Assert.Equal(ErrorCode.DeviceTooSmall, error.Code);
        Assert.Empty(recorder.Offsets);
        Assert.True(report.Targets[1].Succeeded);
    }

    [Fact]
    public async Task ReadBackCorruptionIsReportedWithOffset()
    {
        var image = RandomImage(1_500_000);
        using var inner = Device("corrupt.img", 4 << 20);
        var device = new RecordingDevice(inner) { CorruptReadAtOffset = 64 * 1024 * 3 + 128 * 1024 };

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [device], SmallChunks());

        var result = report.Targets[0];
        Assert.False(result.Succeeded);
        Assert.False(result.Verified);
        var error = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.VerifyMismatch, error.Code);
    }

    [Fact]
    public async Task SourceShorterThanHeadIsWrittenInOneChunk()
    {
        var image = RandomImage(50_000);
        using var device = Device("tiny.img", 1 << 20);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [device], SmallChunks());

        Assert.True(report.AllSucceeded);
        var back = new byte[50_176];
        device.Read(0, back);
        Assert.Equal(image, back.AsSpan(0, image.Length).ToArray());
    }

    [Fact]
    public async Task MixedSectorSizesUseCommonMultiple()
    {
        var image = RandomImage(1_234_567);
        using var a = Device("s512.img", 4 << 20, 512);
        using var b = Device("s4k.img", 4 << 20, 4096);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [a, b], SmallChunks());

        Assert.All(report.Targets, t => Assert.True(t.Succeeded && t.Verified));
    }

    [Fact]
    public async Task UnknownSourceLengthStillWorks()
    {
        var image = RandomImage(777_777);
        using var device = Device("unknown.img", 4 << 20);

        var report = await new RawImageWriter().WriteAsync(new NonSeekableStream(image), null, [device], SmallChunks());

        Assert.True(report.AllSucceeded);
        Assert.Equal(image.Length, report.ImageBytes);
    }

    [Fact]
    public async Task CancellationStopsWithoutHanging()
    {
        var image = RandomImage(8_000_000);
        using var device = Device("cancel.img", 16 << 20);
        using var cts = new CancellationTokenSource();
        var progress = new Progress<RawWriteProgress>(p =>
        {
            if (p.BytesDone > 500_000)
            {
                cts.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RawImageWriter().WriteAsync(new MemoryStream(image), image.Length, [device], SmallChunks(), progress, cts.Token));
    }

    [Fact]
    public async Task EmptySourceWritesNothing()
    {
        using var device = Device("empty.img", 1 << 20);
        var recorder = new RecordingDevice(device);

        var report = await new RawImageWriter().WriteAsync(new MemoryStream(), 0, [recorder], SmallChunks());

        Assert.True(report.AllSucceeded);
        Assert.Equal(0, report.ImageBytes);
        Assert.Empty(recorder.Offsets);
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 10_000));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class RecordingDevice(IBlockDevice inner) : IBlockDevice
    {
        private int _writes;

        public List<long> Offsets { get; } = [];

        public int FailOnWriteNumber { get; set; }

        public long CorruptReadAtOffset { get; set; } = -1;

        public string Name => inner.Name;

        public int SectorSize => inner.SectorSize;

        public long Length => inner.Length;

        public int BufferAlignment => inner.BufferAlignment;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            lock (Offsets)
            {
                Offsets.Add(offset);
            }

            if (FailOnWriteNumber > 0 && Interlocked.Increment(ref _writes) == FailOnWriteNumber)
            {
                throw new IOException("simulated write failure");
            }

            inner.Write(offset, data);
        }

        public int Read(long offset, Span<byte> buffer)
        {
            var read = inner.Read(offset, buffer);
            if (CorruptReadAtOffset >= offset && CorruptReadAtOffset < offset + buffer.Length)
            {
                buffer[(int)(CorruptReadAtOffset - offset)] ^= 0xFF;
            }

            return read;
        }

        public void Flush() => inner.Flush();

        public void Dispose()
        {
        }
    }
}
