// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical.Images;

namespace Bootrix.Core.Tests.Optical;

public class SectorPaddedStreamTests
{
    private static byte[] Data(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2048)]
    [InlineData(2048, 2048)]
    [InlineData(2049, 4096)]
    [InlineData(10_000, 10_240)]
    public void LengthIsRoundedUpToTheSectorSize(int source, int padded)
    {
        using var stream = new SectorPaddedStream(new MemoryStream(Data(source)), source);

        Assert.Equal(padded, stream.Length);
    }

    [Fact]
    public void ContentIsFollowedByZeros()
    {
        var data = Data(3000);
        using var stream = new SectorPaddedStream(new MemoryStream(data), data.Length);

        var all = new byte[stream.Length];
        stream.ReadExactly(all);

        Assert.Equal(data, all[..3000]);
        Assert.All(all[3000..], b => Assert.Equal(0, b));
        Assert.Equal(0, stream.Read(new byte[10]));
    }

    [Fact]
    public void ReadsThatCrossTheContentBoundarySpanBothParts()
    {
        var data = Data(2100);
        using var stream = new SectorPaddedStream(new MemoryStream(data), data.Length);
        stream.Seek(2000, SeekOrigin.Begin);

        var piece = new byte[200];
        stream.ReadExactly(piece);

        Assert.Equal(data[2000..2100], piece[..100]);
        Assert.All(piece[100..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void SeekingBackwardsReReads()
    {
        var data = Data(5000);
        using var stream = new SectorPaddedStream(new MemoryStream(data), data.Length);
        stream.Position = 4000;
        stream.ReadExactly(new byte[500]);

        stream.Seek(100, SeekOrigin.Begin);
        var piece = new byte[50];
        stream.ReadExactly(piece);

        Assert.Equal(data[100..150], piece);
        Assert.Equal(150, stream.Position);
    }

    [Fact]
    public void SeekFromEndWorksInThePadding()
    {
        var data = Data(100);
        using var stream = new SectorPaddedStream(new MemoryStream(data), data.Length);

        stream.Seek(-10, SeekOrigin.End);

        Assert.Equal(2038, stream.Position);
        Assert.Equal(10, stream.Read(new byte[10]));
    }

    [Fact]
    public void ShortSourceIsAnErrorNotSilentPadding()
    {
        // the length says 5000 bytes but the file ends after 3000: a truncated download
        using var stream = new SectorPaddedStream(new MemoryStream(Data(3000)), 5000);

        Assert.Throws<EndOfStreamException>(() => stream.ReadExactly(new byte[5000]));
    }

    [Fact]
    public void SourceThatCannotSeekIsReadSequentially()
    {
        var data = Data(3000);
        using var stream = new SectorPaddedStream(new NonSeekableStream(data), data.Length);

        Assert.False(stream.CanSeek);
        var all = new byte[stream.Length];
        stream.ReadExactly(all);
        Assert.Equal(data, all[..3000]);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void DisposingClosesTheSource()
    {
        var inner = new MemoryStream(Data(10));
        var stream = new SectorPaddedStream(inner, 10);

        stream.Dispose();

        Assert.False(inner.CanRead);
    }

    [Fact]
    public void WritingIsNotAllowed()
    {
        using var stream = new SectorPaddedStream(new MemoryStream(Data(10)), 10);

        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
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

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
