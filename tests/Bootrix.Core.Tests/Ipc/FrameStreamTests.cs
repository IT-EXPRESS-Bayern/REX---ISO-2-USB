// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Ipc;

namespace Bootrix.Core.Tests.Ipc;

public class FrameStreamTests
{
    private static byte[] Header(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }

    [Fact]
    public async Task Frames_SurviveTheRoundTripInOrder()
    {
        var wire = new MemoryStream();
        var writer = new FrameStream(wire);
        await writer.WriteAsync(new byte[] { 1, 2, 3 }, default);
        await writer.WriteAsync(new byte[] { 4 }, default);
        await writer.WriteAsync(new byte[300_000], default);

        wire.Position = 0;
        var reader = new FrameStream(wire);
        Assert.Equal(new byte[] { 1, 2, 3 }, await reader.ReadAsync(default));
        Assert.Equal(new byte[] { 4 }, await reader.ReadAsync(default));
        Assert.Equal(300_000, (await reader.ReadAsync(default))!.Length);
        Assert.Null(await reader.ReadAsync(default));
    }

    [Fact]
    public async Task Header_IsUInt32LittleEndian()
    {
        var wire = new MemoryStream();
        await new FrameStream(wire).WriteAsync(new byte[0x1_0203], default);

        Assert.Equal(new byte[] { 0x03, 0x02, 0x01, 0x00 }, wire.ToArray()[..4]);
    }

    [Fact]
    public async Task Read_FromAStreamThatReturnsTinyChunks_StillAssemblesTheFrame()
    {
        var payload = Enumerable.Range(0, 200_000).Select(i => (byte)i).ToArray();
        var wire = new MemoryStream();
        await new FrameStream(wire).WriteAsync(payload, default);
        wire.Position = 0;

        var frame = await new FrameStream(new TrickleStream(wire, 777)).ReadAsync(default);

        Assert.Equal(payload, frame);
    }

    [Fact]
    public async Task Read_EndBetweenFrames_IsACleanClose()
    {
        Assert.Null(await new FrameStream(new MemoryStream()).ReadAsync(default));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Read_TruncatedHeader_Throws(int bytes)
    {
        var reader = new FrameStream(new MemoryStream(Header(10)[..bytes]));

        await Assert.ThrowsAsync<RpcProtocolException>(async () => await reader.ReadAsync(default));
    }

    [Fact]
    public async Task Read_TruncatedPayload_Throws()
    {
        var reader = new FrameStream(new MemoryStream([.. Header(10), 1, 2, 3]));

        await Assert.ThrowsAsync<RpcProtocolException>(async () => await reader.ReadAsync(default));
    }

    [Fact]
    public async Task Read_FrameOverTheLimit_IsRefusedBeforeItsPayloadIsRead()
    {
        // Only the header is present; a reader that trusted it would wait for or allocate the announced size.
        var reader = new FrameStream(new MemoryStream(Header(101)), maxFrameBytes: 100);

        var ex = await Assert.ThrowsAsync<RpcProtocolException>(async () => await reader.ReadAsync(default));

        Assert.Contains("exceeds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_AnnouncedMaximumWithoutData_FailsAsTruncated()
    {
        var reader = new FrameStream(new MemoryStream([.. Header(RpcConnectionOptions.DefaultMaxFrameBytes), 1]));

        await Assert.ThrowsAsync<RpcProtocolException>(async () => await reader.ReadAsync(default));
    }

    [Fact]
    public async Task Read_EmptyFrame_Throws()
    {
        var reader = new FrameStream(new MemoryStream(Header(0)));

        await Assert.ThrowsAsync<RpcProtocolException>(async () => await reader.ReadAsync(default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Write_OutsideTheLimit_ThrowsAndWritesNothing(int size)
    {
        var wire = new MemoryStream();

        await Assert.ThrowsAsync<RpcProtocolException>(async () => await new FrameStream(wire, 100).WriteAsync(new byte[size], default));

        Assert.Equal(0, wire.Length);
    }

    [Fact]
    public async Task Write_ExactlyTheLimit_IsAccepted()
    {
        var wire = new MemoryStream();
        await new FrameStream(wire, 100).WriteAsync(new byte[100], default);

        Assert.Equal(104, wire.Length);
    }

    [Fact]
    public async Task ConcurrentWriters_NeverInterleaveFrames()
    {
        var wire = new MemoryStream();
        var writer = new FrameStream(wire);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => writer.WriteAsync(Enumerable.Repeat((byte)i, 1000 + i).ToArray(), default))));

        wire.Position = 0;
        var reader = new FrameStream(wire);
        var seen = new HashSet<int>();
        while (await reader.ReadAsync(default) is { } frame)
        {
            Assert.All(frame, b => Assert.Equal(frame[0], b));
            Assert.Equal(1000 + frame[0], frame.Length);
            seen.Add(frame[0]);
        }

        Assert.Equal(50, seen.Count);
    }

    private sealed class TrickleStream(Stream inner, int chunk) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
