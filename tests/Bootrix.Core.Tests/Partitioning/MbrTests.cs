// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Tests.Partitioning;

public class MbrTests
{
    [Fact]
    public void ToBytes_PlacesEveryFieldAtItsOffset()
    {
        var bootstrap = Enumerable.Range(0, 440).Select(i => (byte)i).ToArray();
        var mbr = new MbrBuilder()
            .WithBootstrap(bootstrap)
            .WithSignature(0xDEADBEEF)
            .AddPartition(MbrPartitionType.Fat32Lba, 2048, 100_000, active: true)
            .AddPartition(MbrPartitionType.Linux, 200_000, 50_000)
            .Build();

        var bytes = mbr.ToBytes();

        Assert.Equal(512, bytes.Length);
        Assert.Equal(bootstrap, bytes[..440]);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(440)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(444)));
        Assert.Equal(0x80, bytes[446]);
        Assert.Equal([0x20, 0x21, 0x00], bytes[447..450]);
        Assert.Equal(0x0C, bytes[450]);
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(454)));
        Assert.Equal(100_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(458)));
        Assert.Equal(0x00, bytes[462]);
        Assert.Equal(0x83, bytes[466]);
        Assert.All(bytes[478..510], b => Assert.Equal(0, b));
        Assert.Equal([0x55, 0xAA], bytes[510..]);
    }

    [Fact]
    public void Parse_InvertsToBytes()
    {
        var original = new MbrBuilder()
            .WithSignature(0x12345678)
            .AddPartition(MbrPartitionType.Ntfs, 63, 1_000_000, active: true)
            .AddPartition(MbrPartitionType.EfiSystem, 1_000_063, 4096)
            .AddPartition(MbrPartitionType.LinuxSwap, 2_000_000, 8192)
            .Build();

        var parsed = Mbr.Parse(original.ToBytes());

        Assert.Equal(original.DiskSignature, parsed.DiskSignature);
        Assert.Equal(original.Entries, parsed.Entries);
        Assert.Equal(original.Bootstrap.ToArray(), parsed.Bootstrap.ToArray());
        Assert.True(parsed.Entries[3].IsEmpty);
        Assert.True(parsed.Entries[0].IsActive);
        Assert.False(parsed.IsProtective);
    }

    [Fact]
    public void Build_WithoutBootstrap_WritesTheMissingOperatingSystemStub()
    {
        var bytes = new MbrBuilder().Build().ToBytes();

        Assert.Equal(0xFA, bytes[0]);
        Assert.Contains("Missing operating system", Encoding.ASCII.GetString(bytes, 0, 100), StringComparison.Ordinal);
        Assert.All(bytes[200..440], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Build_ShortBootstrap_IsPaddedWithZeros()
    {
        var bytes = new MbrBuilder().WithBootstrap([1, 2, 3]).Build().ToBytes();

        Assert.Equal([1, 2, 3, 0], bytes[..4]);
    }

    [Fact]
    public void Parse_WithoutSignature_IsRejected()
    {
        var sector = new byte[512];

        Assert.False(Mbr.TryParse(sector, out _));
        Assert.Throws<InvalidDataException>(() => Mbr.Parse(sector));
        Assert.False(Mbr.TryParse(new byte[100], out _));
    }

    [Fact]
    public void Parse_KeepsCopyProtectionMarker()
    {
        var sector = new MbrBuilder().Build().ToBytes();
        sector[444] = 0x5A;
        sector[445] = 0x5A;

        var parsed = Mbr.Parse(sector);

        Assert.Equal(0x5A5A, parsed.Reserved);
        Assert.Equal(sector, parsed.ToBytes());
    }

    [Fact]
    public void Build_FillsChsFromTheGeometry()
    {
        var mbr = new MbrBuilder()
            .WithGeometry(new ChsGeometry(16, 63))
            .AddPartition(MbrPartitionType.Fat16, 63, 100_000)
            .Build();

        Assert.Equal(new ChsAddress(0, 1, 1), mbr.Entries[0].FirstChs);
        Assert.Equal(new ChsAddress(99, 4, 19), mbr.Entries[0].LastChs);
    }

    [Fact]
    public void Build_PartitionBeyondTheChsRange_GetsTheMaximumAddress()
    {
        var mbr = new MbrBuilder().AddPartition(MbrPartitionType.Ntfs, 2048, 100_000_000).Build();

        Assert.Equal(new ChsAddress(0, 32, 33), mbr.Entries[0].FirstChs);
        Assert.Equal(ChsAddress.Unrepresentable, mbr.Entries[0].LastChs);
    }

    [Fact]
    public void Protective_CoversTheDiskFromLbaOne()
    {
        var bytes = MbrBuilder.Protective(131_072).ToBytes();

        Assert.Equal(0x00, bytes[446]);
        Assert.Equal([0x00, 0x02, 0x00], bytes[447..450]);
        Assert.Equal(0xEE, bytes[450]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(454)));
        Assert.Equal(131_071u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(458)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(440)));
        Assert.True(Mbr.Parse(bytes).IsProtective);
    }

    [Fact]
    public void Protective_OnAHugeDisk_ClampsSizeAndEndAddress()
    {
        var mbr = MbrBuilder.Protective(8L * 1024 * 1024 * 1024);

        Assert.Equal(uint.MaxValue, mbr.Entries[0].SectorCount);
        Assert.Equal(ChsAddress.ProtectiveEnd, mbr.Entries[0].LastChs);
        Assert.Equal([0xFF, 0xFF, 0xFF], ToBytes(mbr.Entries[0].LastChs));
    }

    [Fact]
    public void Builder_Rejects_InvalidPartitions()
    {
        Assert.Throws<ArgumentException>(() => new MbrBuilder().AddPartition(0, 2048, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MbrBuilder().AddPartition(0x0C, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MbrBuilder().AddPartition(0x0C, 2048, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MbrBuilder().AddPartition(0x0C, 4_000_000_000, 400_000_000));
        Assert.Throws<ArgumentException>(() => new MbrBuilder().WithBootstrap(new byte[441]));
    }

    [Fact]
    public void Builder_Rejects_FiveOverlapsAndTwoActivePartitions()
    {
        var full = new MbrBuilder();
        for (var i = 0; i < 4; i++)
        {
            full.AddPartition(0x83, 2048 + i * 1000, 1000);
        }

        Assert.Throws<InvalidOperationException>(() => full.AddPartition(0x83, 100_000, 10));

        var overlapping = new MbrBuilder().AddPartition(0x83, 2048, 1000);
        Assert.Throws<InvalidOperationException>(() => overlapping.AddPartition(0x83, 2500, 1000));
        overlapping.AddPartition(0x83, 3048, 1000);

        var twoActive = new MbrBuilder().AddPartition(0x83, 2048, 1000, active: true);
        Assert.Throws<InvalidOperationException>(() => twoActive.AddPartition(0x83, 4000, 1000, active: true));
    }

    [Fact]
    public void MbrPartitionType_Constants_HaveTheirDocumentedValues()
    {
        Assert.Equal(0x01, MbrPartitionType.Fat12);
        Assert.Equal(0x04, MbrPartitionType.Fat16Small);
        Assert.Equal(0x06, MbrPartitionType.Fat16);
        Assert.Equal(0x07, MbrPartitionType.Ntfs);
        Assert.Equal(0x0B, MbrPartitionType.Fat32Chs);
        Assert.Equal(0x0C, MbrPartitionType.Fat32Lba);
        Assert.Equal(0x0E, MbrPartitionType.Fat16Lba);
        Assert.Equal(0x0F, MbrPartitionType.ExtendedLba);
        Assert.Equal(0x83, MbrPartitionType.Linux);
        Assert.Equal(0xAF, MbrPartitionType.AppleHfs);
        Assert.Equal(0xEE, MbrPartitionType.GptProtective);
        Assert.Equal(0xEF, MbrPartitionType.EfiSystem);
        Assert.True(MbrPartitionType.IsExtended(0x05));
        Assert.True(MbrPartitionType.IsExtended(0x0F));
        Assert.False(MbrPartitionType.IsExtended(0x83));
    }

    private static byte[] ToBytes(ChsAddress address)
    {
        var bytes = new byte[3];
        address.WriteTo(bytes);
        return bytes;
    }
}
