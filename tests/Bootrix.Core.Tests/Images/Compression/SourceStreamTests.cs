// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Compression;

namespace Bootrix.Core.Tests.Images.Compression;

public sealed class SourceStreamTests
{
    private static byte[] Sequence(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i % 251))];

    /// <summary>Hands out at most <paramref name="chunk"/> bytes per read, like a network stream does.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : Stream
    {
        private int _position;

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunk), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public void Read_FillsTheBufferEvenWhenTheSourceTrickles(int chunk)
    {
        var data = Sequence(10_000);
        using var source = new SourceStream(new TrickleStream(data, chunk), leaveOpen: false);
        var buffer = new byte[5000];

        var read = source.Read(buffer, 0, buffer.Length);

        Assert.Equal(5000, read);
        Assert.Equal(data[..5000], buffer);
        Assert.Equal(5000, source.Position);
    }

    [Fact]
    public async Task ReadAsync_FillsTheBufferEvenWhenTheSourceTrickles()
    {
        var data = Sequence(10_000);
        using var source = new SourceStream(new TrickleStream(data, 3), leaveOpen: false);
        var buffer = new byte[4000];

        var read = await source.ReadAsync(buffer);

        Assert.Equal(4000, read);
        Assert.Equal(data[..4000], buffer);
    }

    [Fact]
    public void Read_AtTheEnd_ReturnsWhatIsLeftThenZero()
    {
        using var source = new SourceStream(new TrickleStream(Sequence(10), 4), leaveOpen: false);
        var buffer = new byte[64];

        Assert.Equal(10, source.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, source.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Rewind_ReplaysTheBytesWithoutTouchingTheSourceAgain()
    {
        var data = Sequence(1000);
        var trickle = new TrickleStream(data, 100);
        using var source = new SourceStream(trickle, leaveOpen: false);
        var first = new byte[300];
        source.ReadExactly(first);

        source.Rewind(200);

        Assert.Equal(100, source.Position);
        Assert.Equal(300, source.Pulled);
        var again = new byte[250];
        source.ReadExactly(again);
        Assert.Equal(data[100..350], again);
        Assert.Equal(350, source.Position);
        Assert.Equal(350, source.Pulled);
    }

    [Fact]
    public void Rewind_WorksAcrossTheWrapOfTheRememberedWindow()
    {
        var data = Sequence(100);
        using var source = new SourceStream(new TrickleStream(data, 5), leaveOpen: false, windowSize: 8);
        source.ReadExactly(new byte[20]);

        Assert.Equal(8, source.Rewindable);
        source.Rewind(8);

        var again = new byte[8];
        source.ReadExactly(again);
        Assert.Equal(data[12..20], again);
    }

    [Fact]
    public void Rewind_BeyondTheWindow_Throws()
    {
        using var source = new SourceStream(new TrickleStream(Sequence(100), 5), leaveOpen: false, windowSize: 8);
        source.ReadExactly(new byte[20]);

        Assert.Throws<InvalidOperationException>(() => source.Rewind(9));
        Assert.Throws<InvalidOperationException>(() => source.Rewind(-1));
    }

    [Fact]
    public void Rewind_ABigReadThatExceededTheWindow_KeepsTheTail()
    {
        var data = Sequence(100);
        using var source = new SourceStream(new TrickleStream(data, 100), leaveOpen: false, windowSize: 16);
        source.ReadExactly(new byte[50]);

        Assert.Equal(16, source.Rewindable);
        source.Rewind(16);
        var again = new byte[16];
        source.ReadExactly(again);

        Assert.Equal(data[34..50], again);
    }

    [Fact]
    public void PeekByte_LooksAtTheNextByteWithoutConsumingIt()
    {
        using var source = new SourceStream(new TrickleStream([7, 8], 1), leaveOpen: false);

        Assert.Equal(7, source.PeekByte());
        Assert.Equal(7, source.PeekByte());
        Assert.Equal(0, source.Position);
        Assert.Equal(7, source.ReadByte());
        Assert.Equal(8, source.ReadByte());
        Assert.Equal(-1, source.PeekByte());
    }

    [Fact]
    public void Borrow_CanBeDisposedWithoutClosingTheOwner()
    {
        var trickle = new TrickleStream(Sequence(100), 10);
        using var source = new SourceStream(trickle, leaveOpen: false);

        using (var borrowed = source.Borrow())
        {
            Assert.Equal(0, borrowed.ReadByte());
        }

        Assert.False(trickle.Disposed);
        Assert.Equal(1, source.ReadByte());
        Assert.Equal(2, source.Position);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Dispose_ClosesTheSourceUnlessLeftOpen(bool leaveOpen, bool expectedDisposed)
    {
        var trickle = new TrickleStream(Sequence(10), 10);
        var source = new SourceStream(trickle, leaveOpen);

        source.Dispose();

        Assert.Equal(expectedDisposed, trickle.Disposed);
    }

    [Fact]
    public void Borrow_ByDefaultReportsTheEndOfTheDataAsMinusOne()
    {
        using var source = new SourceStream(new TrickleStream([5], 1), leaveOpen: false);
        using var borrowed = source.Borrow();

        Assert.Equal(5, borrowed.ReadByte());
        Assert.Equal(-1, borrowed.ReadByte());
    }

    [Fact]
    public void Borrow_FailingAtTheEnd_ThrowsOnASingleByteReadPastTheData()
    {
        using var source = new SourceStream(new TrickleStream([5], 1), leaveOpen: false);
        using var borrowed = source.Borrow(failAtEnd: true);

        Assert.Equal(5, borrowed.ReadByte());
        Assert.Throws<EndOfStreamException>(() => borrowed.ReadByte());

        // Block reads keep the usual contract: zero bytes means the end.
        Assert.Equal(0, borrowed.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void NotSeekableAndNotWritable()
    {
        using var source = new SourceStream(new TrickleStream([], 1), leaveOpen: false);

        Assert.False(source.CanSeek);
        Assert.False(source.CanWrite);
        Assert.Throws<NotSupportedException>(() => source.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => source.Write([1], 0, 1));
        Assert.Throws<NotSupportedException>(() => source.Length);
    }
}
