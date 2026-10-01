// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Optical;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;

namespace Bootrix.Windows.Tests.Optical;

/// <summary>Calls the IStream implementation directly, the way IMAPI would through its vtable.</summary>
public unsafe class ManagedStreamTests
{
    private static byte[] Data(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    private static IStream Wrap(Stream stream) => new ManagedStream(stream);

    private static (HRESULT Result, uint Read) Read(IStream stream, byte[] buffer)
    {
        uint read = 0;
        fixed (byte* pointer = buffer)
        {
            var result = stream.Read(pointer, (uint)buffer.Length, &read);
            return (result, read);
        }
    }

    [Fact]
    public void ReadsTheContentInOrder()
    {
        var data = Data(10_000);
        var stream = Wrap(new MemoryStream(data));

        var first = new byte[4096];
        var (result, read) = Read(stream, first);

        Assert.Equal(0, result.Value);
        Assert.Equal(4096u, read);
        Assert.Equal(data[..4096], first);

        var second = new byte[4096];
        Read(stream, second);
        Assert.Equal(data[4096..8192], second);
    }

    [Fact]
    public void ShortSourceReadsAreCompleted()
    {
        var data = Data(8192);
        var stream = Wrap(new TrickleStream(data, 100));

        var buffer = new byte[3000];
        var (_, read) = Read(stream, buffer);

        Assert.Equal(3000u, read);
        Assert.Equal(data[..3000], buffer);
    }

    [Fact]
    public void ReadAtTheEndReturnsWhatIsLeft()
    {
        var stream = Wrap(new MemoryStream(Data(2048 + 100)));
        Read(stream, new byte[2048]);

        var (result, read) = Read(stream, new byte[2048]);

        Assert.Equal(0, result.Value);
        Assert.Equal(100u, read);
        Assert.Equal(0u, Read(stream, new byte[10]).Read);
    }

    [Fact]
    public void ReadErrorIsReportedAsAStorageFault()
    {
        var stream = Wrap(new FailingStream());

        var (result, read) = Read(stream, new byte[512]);

        Assert.Equal(unchecked((int)0x8003001E), result.Value);
        Assert.Equal(0u, read);
    }

    [Fact]
    public void SeekMovesAndReportsThePosition()
    {
        var data = Data(5000);
        var stream = Wrap(new MemoryStream(data));
        ulong position;

        stream.Seek(1000, SeekOrigin.Begin, &position);
        Assert.Equal(1000ul, position);

        stream.Seek(500, SeekOrigin.Current, &position);
        Assert.Equal(1500ul, position);

        stream.Seek(-100, SeekOrigin.End, &position);
        Assert.Equal(4900ul, position);

        var buffer = new byte[100];
        Read(stream, buffer);
        Assert.Equal(data[4900..], buffer);
    }

    [Fact]
    public void SeekWithoutOutputPositionIsFine()
    {
        var stream = Wrap(new MemoryStream(Data(100)));

        stream.Seek(10, SeekOrigin.Begin, null);
    }

    [Fact]
    public void StatReportsTheSizeAndTheStreamType()
    {
        var stream = Wrap(new MemoryStream(Data(123_456)));
        STATSTG stat;

        stream.Stat(&stat, STATFLAG.STATFLAG_NONAME);

        Assert.Equal(123_456ul, stat.cbSize);
        Assert.Equal(2u, stat.type);
        Assert.True(stat.pwcsName.Value is null);
    }

    [Fact]
    public void WritingIsDenied()
    {
        var stream = Wrap(new MemoryStream(Data(100)));
        uint written = 0;
        var bytes = new byte[10];

        fixed (byte* pointer = bytes)
        {
            Assert.Equal(unchecked((int)0x80030005), stream.Write(pointer, 10, &written).Value);
        }

        Assert.Throws<IOException>(() => stream.SetSize(5));
    }

    [Fact]
    public void UnsupportedOperationsFailWithAStorageCode()
    {
        var stream = Wrap(new MemoryStream(Data(100)));

        var clone = Assert.Throws<IOException>(() => stream.Clone(out _));
        var lockRegion = Assert.Throws<IOException>(() => stream.LockRegion(0, 10, LOCKTYPE.LOCK_WRITE));

        Assert.Equal(unchecked((int)0x80030001), clone.HResult);
        Assert.Equal(unchecked((int)0x80030001), lockRegion.HResult);
        stream.Commit(STGC.STGC_DEFAULT);
        stream.Revert();
    }

    [Fact]
    public void ImapiStreamNeedsWholeSectors()
    {
        Assert.Throws<ArgumentException>(() => ImapiStream.FromManaged(new MemoryStream(new byte[3000])));

        using var good = ImapiStream.FromManaged(new MemoryStream(new byte[4 * 2048]));
        Assert.Equal(4, good.Sectors);
    }

    [Fact]
    public void ImapiStreamClosesTheManagedStreamWhenDisposed()
    {
        var inner = new MemoryStream(new byte[2048]);

        ImapiStream.FromManaged(inner).Dispose();

        Assert.False(inner.CanRead);
    }

    private sealed class TrickleStream(byte[] data, int chunk) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, chunk));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => 4096;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read error");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => offset;

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
