// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Storage;
using Bootrix.Windows.Storage;

namespace Bootrix.Windows.Tests.Storage;

public class DriveLayoutReaderTests
{
    private static readonly Guid Esp = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    private static readonly Guid Ldm = new("af9b60a0-1431-4f62-bc68-3311714a69ad");

    private static byte[] Header(uint style, uint count, int entries)
    {
        var data = new byte[48 + 144 * entries];
        BinaryPrimitives.WriteUInt32LittleEndian(data, style);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), count);
        return data;
    }

    private static void WriteGptEntry(byte[] data, int index, Guid type, long offset, long length, string name, uint number)
    {
        var entry = data.AsSpan(48 + 144 * index, 144);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 1);
        BinaryPrimitives.WriteInt64LittleEndian(entry[8..], offset);
        BinaryPrimitives.WriteInt64LittleEndian(entry[16..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], number);
        type.TryWriteBytes(entry[32..]);
        Guid.NewGuid().TryWriteBytes(entry[48..]);
        Encoding.Unicode.GetBytes(name, entry.Slice(72, 72));
    }

    private static void WriteMbrEntry(byte[] data, int index, byte type, long offset, long length, uint number)
    {
        var entry = data.AsSpan(48 + 144 * index, 144);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 0);
        BinaryPrimitives.WriteInt64LittleEndian(entry[8..], offset);
        BinaryPrimitives.WriteInt64LittleEndian(entry[16..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], number);
        entry[32] = type;
    }

    [Fact]
    public void ParsesGptLayout()
    {
        var data = Header(1, 2, 2);
        var diskId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        diskId.TryWriteBytes(data.AsSpan(8));
        WriteGptEntry(data, 0, Esp, 1_048_576, 300_000_000, "EFI system partition", 1);
        WriteGptEntry(data, 1, BasicData, 400_000_000, 8_000_000_000, "Data", 2);

        var layout = DriveLayoutReader.Parse(data);

        Assert.Equal(DiskPartitionStyle.Gpt, layout.Style);
        Assert.Equal(diskId.ToString("D"), layout.Signature);
        Assert.Equal(2, layout.Partitions.Count);
        Assert.True(layout.Partitions[0].IsEfiSystem);
        Assert.Equal("EFI system partition", layout.Partitions[0].Name);
        Assert.Equal(8_000_000_000, layout.Partitions[1].Length);
        Assert.False(layout.Partitions[1].IsManagedByOtherStack);
    }

    [Fact]
    public void SkipsEmptyMbrEntries()
    {
        var data = Header(0, 4, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0xCAFEBABE);
        WriteMbrEntry(data, 0, 0x0C, 1_048_576, 15_000_000_000, 1);

        var layout = DriveLayoutReader.Parse(data);

        Assert.Equal(DiskPartitionStyle.Mbr, layout.Style);
        Assert.Equal("CAFEBABE", layout.Signature);
        var partition = Assert.Single(layout.Partitions);
        Assert.Equal("0x0C", partition.Type);
    }

    [Fact]
    public void DetectsDynamicDisks()
    {
        var data = Header(1, 1, 1);
        WriteGptEntry(data, 0, Ldm, 1_048_576, 1_000_000_000, "LDM", 1);

        Assert.True(DriveLayoutReader.Parse(data).Partitions[0].IsManagedByOtherStack);

        var mbr = Header(0, 4, 4);
        WriteMbrEntry(mbr, 0, 0x42, 1_048_576, 1_000_000_000, 1);

        Assert.True(DriveLayoutReader.Parse(mbr).Partitions[0].IsManagedByOtherStack);
    }

    [Fact]
    public void TruncatedBufferYieldsRawDisk()
    {
        var layout = DriveLayoutReader.Parse(new byte[10]);

        Assert.Equal(DiskPartitionStyle.Raw, layout.Style);
        Assert.Empty(layout.Partitions);
    }

    [Fact]
    public void ClaimedCountBeyondBufferIsIgnored()
    {
        var data = Header(1, 128, 1);
        WriteGptEntry(data, 0, BasicData, 1_048_576, 1_000_000, "x", 1);

        Assert.Single(DriveLayoutReader.Parse(data).Partitions);
    }
}
