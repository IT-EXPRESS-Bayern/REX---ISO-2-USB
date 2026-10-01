// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Writing.Raw;
using Bootrix.Core.Writing.Verify;

namespace Bootrix.Core.Tests.Writing.Verify;

public sealed class RawVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-verify-" + Guid.NewGuid().ToString("N"));

    public RawVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Image(int length, int seed = 3)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private FileBlockDevice Device(byte[] image, long size = 4 << 20, int sectorSize = 512)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".img");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            file.SetLength(size);
            file.Write(image);
        }

        return new FileBlockDevice(path, size, sectorSize, create: false);
    }

    [Fact]
    public async Task IdenticalMedium_Matches_AndTheTailBeyondTheImageIsNotLookedAt()
    {
        var image = Image(1_000_003);
        using var device = Device(image);
        device.Write(1_000_448, Enumerable.Repeat((byte)0xAA, 512).ToArray());

        var report = await RawVerifier.CompareAsync(new MemoryStream(image), image.Length, device, chunkSize: 64 * 1024);

        Assert.True(report.Matches);
        Assert.Equal(image.Length, report.BytesCompared);
        Assert.Null(report.ToException("stick"));
    }

    [Fact]
    public async Task ChangedBytes_AreReportedWithTheirOffsets()
    {
        var image = Image(900_000);
        using var device = Device(image);
        var damaged = new byte[512];
        device.Read(262_144, damaged);
        damaged[17] ^= 0xFF;
        device.Write(262_144, damaged);
        device.Read(819_200, damaged);
        damaged[0] ^= 0x01;
        device.Write(819_200, damaged);

        var report = await RawVerifier.CompareAsync(new MemoryStream(image), image.Length, device, chunkSize: 128 * 1024);

        Assert.False(report.Matches);
        Assert.Equal(2, report.DifferingBlocks);
        Assert.Equal([262_144L + 17, 819_200L], report.FirstOffsets);
        var error = report.ToException("stick")!;
        Assert.Equal(ErrorCode.VerifyMismatch, error.Code);
        Assert.Equal(262_161L, error.Arguments[0]);
        Assert.Contains("0x40011", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyTheFirstOffsetsAreListed_ButAllBlocksAreCounted()
    {
        var image = Image(1 << 20);
        var wrong = Image(1 << 20, seed: 99);
        using var device = Device(wrong);

        var report = await RawVerifier.CompareAsync(new MemoryStream(image), image.Length, device);

        Assert.Equal(256, report.DifferingBlocks);
        Assert.Equal(16, report.FirstOffsets.Count);
    }

    [Fact]
    public async Task ImageOfUnknownLength_IsComparedToItsEnd()
    {
        var image = Image(700_000);
        using var device = Device(image);

        var report = await RawVerifier.CompareAsync(new TrickleStream(image), null, device, chunkSize: 100_000);

        Assert.True(report.Matches);
        Assert.Equal(700_000, report.BytesCompared);
    }

    [Fact]
    public async Task ImageLongerThanTheMedium_IsTooLarge()
    {
        var image = Image(2 << 20);
        using var device = Device(image[..(1 << 20)], size: 1 << 20);

        var error = await Assert.ThrowsAsync<BootrixException>(() => RawVerifier.CompareAsync(new MemoryStream(image), image.Length, device));

        Assert.Equal(ErrorCode.DeviceTooSmall, error.Code);
    }

    [Fact]
    public async Task UnknownLengthLongerThanTheMedium_ReportsTheMissingPartAsDifferent()
    {
        var image = Image(2 << 20);
        using var device = Device(image[..(1 << 20)], size: 1 << 20);

        var report = await RawVerifier.CompareAsync(new TrickleStream(image), null, device);

        Assert.False(report.Matches);
        Assert.Equal(256, report.DifferingBlocks);
        Assert.Equal(1L << 20, report.FirstOffsets[0]);
    }

    [Fact]
    public async Task WithABlockMap_OnlyTheMappedBlocksCount()
    {
        var image = BmaptoolFixture.Image();
        using var device = Device(image, size: 16 << 20);
        device.Write(2 << 20, Enumerable.Repeat((byte)0x55, 4096).ToArray());
        var map = Core.Writing.Raw.BlockMapParser.Parse(BmaptoolFixture.BlockMapBytes).ToWriteMap(fillGaps: false);

        var sparse = await RawVerifier.CompareAsync(new MemoryStream(image), image.Length, device, map);
        var full = await RawVerifier.CompareAsync(new MemoryStream(image), image.Length, device);

        Assert.True(sparse.Matches);
        Assert.Equal(9 * 4096, sparse.BytesCompared);
        Assert.Equal(image.Length - (9 * 4096), sparse.BytesSkipped);
        Assert.False(full.Matches);
        Assert.Equal(1, full.DifferingBlocks);
    }

    [Fact]
    public async Task MixedSectorSizeMedium_IsComparedLikeAnyOther()
    {
        var image = Image(1_234_567);
        using var device = Device(image, sectorSize: 4096);

        var report = await RawVerifier.CompareAsync(new MemoryStream(image), image.Length, device, chunkSize: 100_000);

        Assert.True(report.Matches);
    }

    [Fact]
    public async Task Cancellation_StopsTheComparison()
    {
        var image = Image(8 << 20);
        using var device = Device(image, size: 8 << 20);
        using var cts = new CancellationTokenSource();
        var progress = new Progress<RawWriteProgress>(p => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RawVerifier.CompareAsync(new MemoryStream(image), image.Length, device, progress: progress, chunkSize: 256 * 1024, cancellationToken: cts.Token));
    }

    /// <summary>A stream that neither seeks nor knows its length and hands out small pieces, like a decoder does.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
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

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 7777));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
