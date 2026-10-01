// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.IO;

namespace Bootrix.Core.Tests.IO;

public class StreamSliceTests
{
    private static MemoryStream Backing(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        return new MemoryStream(bytes);
    }

    [Fact]
    public void Read_ReturnsBytesFromTheWindowOnly()
    {
        using var backing = Backing(256);
        using var slice = new StreamSlice(backing, 100, 16);

        var buffer = new byte[16];
        var read = slice.Read(buffer, 0, buffer.Length);

        Assert.Equal(16, read);
        Assert.Equal(100, buffer[0]);
        Assert.Equal(115, buffer[15]);
    }

    [Fact]
    public void Read_PastTheEnd_IsTruncated()
    {
        using var backing = Backing(256);
        using var slice = new StreamSlice(backing, 100, 16);
        slice.Position = 10;

        var buffer = new byte[32];

        Assert.Equal(6, slice.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, slice.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Write_LandsAtTheOffsetOfTheWindow()
    {
        using var backing = Backing(64);
        using var slice = new StreamSlice(backing, 32, 8);

        slice.Write([0xAA, 0xBB, 0xCC], 0, 3);

        var raw = backing.ToArray();
        Assert.Equal(0xAA, raw[32]);
        Assert.Equal(0xCC, raw[34]);
        Assert.Equal(31, raw[31]);
        Assert.Equal(35, raw[35]);
    }

    [Fact]
    public void Write_PastTheEnd_ThrowsAndLeavesNeighboursAlone()
    {
        using var backing = Backing(64);
        using var slice = new StreamSlice(backing, 32, 8);
        slice.Position = 6;

        Assert.Throws<IOException>(() => slice.Write(new byte[4], 0, 4));

        Assert.Equal(40, backing.ToArray()[40]);
    }

    [Fact]
    public void Seek_SupportsAllOriginsRelativeToTheWindow()
    {
        using var backing = Backing(64);
        using var slice = new StreamSlice(backing, 16, 20);

        Assert.Equal(5, slice.Seek(5, SeekOrigin.Begin));
        Assert.Equal(8, slice.Seek(3, SeekOrigin.Current));
        Assert.Equal(18, slice.Seek(-2, SeekOrigin.End));
        Assert.Equal(20, slice.Length);
        Assert.Throws<IOException>(() => slice.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public void Position_CanBeSetPastTheEnd_AndReadsNothingThere()
    {
        using var backing = Backing(64);
        using var slice = new StreamSlice(backing, 16, 20);
        slice.Position = 25;

        Assert.Equal(0, slice.Read(new byte[4], 0, 4));
    }

    [Fact]
    public async Task AsyncAccess_BehavesLikeSynchronousAccess()
    {
        using var backing = Backing(64);
        using var slice = new StreamSlice(backing, 8, 8);

        await slice.WriteAsync(new byte[] { 1, 2, 3, 4 });
        slice.Position = 2;
        var buffer = new byte[16];
        var read = await slice.ReadAsync(buffer);

        Assert.Equal(6, read);
        Assert.Equal([3, 4, 12, 13, 14, 15], buffer[..6]);
    }

    [Fact]
    public void SetLength_IsNotSupported()
    {
        using var backing = Backing(8);
        using var slice = new StreamSlice(backing, 0, 4);

        Assert.Throws<NotSupportedException>(() => slice.SetLength(2));
    }

    [Fact]
    public void Constructor_RejectsInvalidWindows()
    {
        using var backing = Backing(8);

        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamSlice(backing, -1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamSlice(backing, 0, -4));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StreamSlice(backing, long.MaxValue, 1));
    }

    [Fact]
    public void Dispose_KeepsTheUnderlyingStreamOpenUnlessAsked()
    {
        using var backing = Backing(8);
        new StreamSlice(backing, 0, 4).Dispose();

        Assert.True(backing.CanRead);

        new StreamSlice(backing, 0, 4, leaveOpen: false).Dispose();

        Assert.False(backing.CanRead);
    }

    [Fact]
    public void Offsets_BeyondFourGigabytes_AreHandledAsLong()
    {
        using var backing = new SparseStream();
        const long offset = 5L * 1024 * 1024 * 1024;
        using var slice = new StreamSlice(backing, offset, 4096);

        slice.Write([7, 7, 7], 0, 3);

        Assert.Equal(offset, backing.LastWriteOffset);
    }

    private sealed class SparseStream : Stream
    {
        public long LastWriteOffset { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => true;

        public override long Length => long.MaxValue;

        public override long Position { get; set; }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => Position = offset;

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            LastWriteOffset = Position;
            Position += count;
        }
    }
}
