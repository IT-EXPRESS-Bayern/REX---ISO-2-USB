// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Partitioning;

public class GptTests
{
    private static readonly Guid DiskId = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid PartitionId = new("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    [Theory]
    [InlineData(512, 131_072, 34, 131_038, 131_039, 131_071)]
    [InlineData(4096, 16_384, 6, 16_378, 16_379, 16_383)]
    public void Build_PlacesHeadersAndEntryArraysAsTheSpecificationDemands(
        int sectorSize, long total, long firstUsable, long lastUsable, long backupEntries, long backupHeader)
    {
        var image = new GptBuilder(total, sectorSize).WithDiskGuid(DiskId).Build();

        Assert.Equal(1, GptImage.PrimaryHeaderLba);
        Assert.Equal(2, GptImage.PrimaryEntriesLba);
        Assert.Equal(backupEntries, image.BackupEntriesLba);
        Assert.Equal(backupHeader, image.BackupHeaderLba);

        var primary = image.PrimaryHeader.ToArray();
        Assert.Equal(sectorSize, primary.Length);
        Assert.Equal("EFI PART", Encoding.ASCII.GetString(primary, 0, 8));
        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32LittleEndian(primary.AsSpan(8)));
        Assert.Equal(92u, BinaryPrimitives.ReadUInt32LittleEndian(primary.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(primary.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadInt64LittleEndian(primary.AsSpan(24)));
        Assert.Equal(backupHeader, BinaryPrimitives.ReadInt64LittleEndian(primary.AsSpan(32)));
        Assert.Equal(firstUsable, BinaryPrimitives.ReadInt64LittleEndian(primary.AsSpan(40)));
        Assert.Equal(lastUsable, BinaryPrimitives.ReadInt64LittleEndian(primary.AsSpan(48)));
        Assert.Equal(DiskId, new Guid(primary.AsSpan(56, 16)));
        Assert.Equal(2, BinaryPrimitives.ReadInt64LittleEndian(primary.AsSpan(72)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(primary.AsSpan(80)));
        Assert.Equal(128u, BinaryPrimitives.ReadUInt32LittleEndian(primary.AsSpan(84)));
        Assert.All(primary[92..], b => Assert.Equal(0, b));

        var backup = image.BackupHeader.ToArray();
        Assert.Equal(backupHeader, BinaryPrimitives.ReadInt64LittleEndian(backup.AsSpan(24)));
        Assert.Equal(1, BinaryPrimitives.ReadInt64LittleEndian(backup.AsSpan(32)));
        Assert.Equal(backupEntries, BinaryPrimitives.ReadInt64LittleEndian(backup.AsSpan(72)));
        Assert.Equal(16384, image.EntryArray.Length);
    }

    [Fact]
    public void Build_CrcFieldsMatchAnIndependentBitwiseImplementation()
    {
        var image = new GptBuilder(131_072)
            .WithDiskGuid(DiskId)
            .AddPartition(GptTypes.EfiSystem, 2048, 22_527, "ESP", uniqueGuid: PartitionId)
            .AddPartition(GptTypes.BasicData, 22_528, 100_000, "Data")
            .Build();

        var entryCrc = ReferenceCrc32(image.EntryArray.Span);
        foreach (var header in new[] { image.PrimaryHeader.ToArray(), image.BackupHeader.ToArray() })
        {
            Assert.Equal(entryCrc, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88)));

            var stored = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            var zeroed = header[..92].ToArray();
            zeroed.AsSpan(16, 4).Clear();
            Assert.Equal(ReferenceCrc32(zeroed), stored);
        }
    }

    [Fact]
    public void Build_WritesGuidsInTheMixedEndianDiskLayout()
    {
        var image = new GptBuilder(131_072)
            .AddPartition(GptTypes.EfiSystem, 2048, 4095, uniqueGuid: PartitionId)
            .AddPartition(GptTypes.BasicData, 4096, 8191)
            .Build();
        var entries = image.EntryArray.ToArray();

        Assert.Equal(
            [0x28, 0x73, 0x2A, 0xC1, 0x1F, 0xF8, 0xD2, 0x11, 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B],
            entries[..16]);
        Assert.Equal(
            [0xA2, 0xA0, 0xD0, 0xEB, 0xE5, 0xB9, 0x33, 0x44, 0x87, 0xC0, 0x68, 0xB6, 0xB7, 0x26, 0x99, 0xC7],
            entries[128..144]);
        Assert.Equal(
            [0xAA, 0xAA, 0xAA, 0xAA, 0xBB, 0xBB, 0xCC, 0xCC, 0xDD, 0xDD, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE, 0xEE],
            entries[16..32]);
    }

    [Fact]
    public void Build_WritesEntryFieldsAtTheirOffsets()
    {
        const GptAttributes attributes = GptAttributes.RequiredPartition | GptAttributes.LegacyBiosBootable | GptAttributes.NoDriveLetter;
        var image = new GptBuilder(131_072)
            .AddPartition(GptTypes.LinuxData, 2048, 4095, "Größe €", attributes)
            .Build();
        var entry = image.EntryArray.Span[..128];

        Assert.Equal(2048, BinaryPrimitives.ReadInt64LittleEndian(entry[32..]));
        Assert.Equal(4095, BinaryPrimitives.ReadInt64LittleEndian(entry[40..]));
        Assert.Equal(0x8000000000000005UL, BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]));
        Assert.Equal("Größe €", Encoding.Unicode.GetString(entry.Slice(56, 14)));
        Assert.All(entry[70..].ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Attributes_HaveTheDocumentedBits()
    {
        Assert.Equal(0x1UL, (ulong)GptAttributes.RequiredPartition);
        Assert.Equal(0x2UL, (ulong)GptAttributes.NoBlockIoProtocol);
        Assert.Equal(0x4UL, (ulong)GptAttributes.LegacyBiosBootable);
        Assert.Equal(0x1000000000000000UL, (ulong)GptAttributes.ReadOnly);
        Assert.Equal(0x2000000000000000UL, (ulong)GptAttributes.ShadowCopy);
        Assert.Equal(0x4000000000000000UL, (ulong)GptAttributes.Hidden);
        Assert.Equal(0x8000000000000000UL, (ulong)GptAttributes.NoDriveLetter);
    }

    [Fact]
    public void WriteTo_CreatesAReadableTableWithBothCopies()
    {
        var disk = new SparseMemoryStream(131_072L * 512);
        new GptBuilder(131_072)
            .WithDiskGuid(DiskId)
            .AddPartition(GptTypes.EfiSystem, 2048, 22_527, "EFI", uniqueGuid: PartitionId)
            .AddPartition(GptTypes.LinuxData, 22_528, 100_000, "root", GptAttributes.NoBlockIoProtocol)
            .Build()
            .WriteTo(disk);

        var gpt = Gpt.Read(disk, 512);

        Assert.NotNull(gpt);
        Assert.True(gpt.PrimaryValid);
        Assert.True(gpt.BackupValid);
        Assert.Equal(DiskId, gpt.DiskGuid);
        Assert.Equal(34, gpt.FirstUsableLba);
        Assert.Equal(131_038, gpt.LastUsableLba);
        Assert.Equal(131_072, gpt.TotalSectors);
        Assert.Equal(128, gpt.EntryCount);
        Assert.Equal(2, gpt.Partitions.Count);
        Assert.Equal(new GptPartition(GptTypes.EfiSystem, PartitionId, 2048, 22_527, GptAttributes.None, "EFI"), gpt.Partitions[0]);
        Assert.Equal(GptTypes.LinuxData, gpt.Partitions[1].TypeGuid);
        Assert.Equal("root", gpt.Partitions[1].Name);
        Assert.Equal(GptAttributes.NoBlockIoProtocol, gpt.Partitions[1].Attributes);
        Assert.Equal(77_473, gpt.Partitions[1].SectorCount);
        Assert.True(Mbr.Parse(Sector(disk, 0)).IsProtective);
    }

    [Fact]
    public void Read_FallsBackToTheBackupWhenThePrimaryHeaderIsDamaged()
    {
        var disk = Written(out var total);
        disk.Position = 512 + 40;
        disk.WriteByte(0xFF);

        var gpt = Gpt.Read(disk, 512);

        Assert.NotNull(gpt);
        Assert.False(gpt.PrimaryValid);
        Assert.True(gpt.BackupValid);
        Assert.Equal(2, gpt.Partitions.Count);
        Assert.Equal(total, gpt.TotalSectors);
    }

    [Fact]
    public void Read_FallsBackToTheBackupWhenThePrimaryEntryArrayIsDamaged()
    {
        var disk = Written(out _);
        disk.Position = (2 * 512) + 5;
        disk.WriteByte(0x99);

        var gpt = Gpt.Read(disk, 512);

        Assert.NotNull(gpt);
        Assert.False(gpt.PrimaryValid);
        Assert.True(gpt.BackupValid);
        Assert.Equal(2, gpt.Partitions.Count);
    }

    [Fact]
    public void Read_WhenOnlyThePrimarySurvives_StillSucceeds()
    {
        var disk = Written(out var total);
        disk.Position = (total - 1) * 512;
        disk.Write(new byte[512]);

        var gpt = Gpt.Read(disk, 512);

        Assert.NotNull(gpt);
        Assert.True(gpt.PrimaryValid);
        Assert.False(gpt.BackupValid);
    }

    [Fact]
    public void Read_WhenBothCopiesAreBroken_ReturnsNull()
    {
        var disk = Written(out var total);
        disk.Position = 512 + 40;
        disk.WriteByte(0xFF);
        disk.Position = ((total - 1) * 512) + 40;
        disk.WriteByte(0xFF);

        Assert.Null(Gpt.Read(disk, 512));
    }

    [Fact]
    public void Read_OfADiskWithoutGpt_ReturnsNull()
    {
        Assert.Null(Gpt.Read(new SparseMemoryStream(64 * 1024 * 1024), 512));
        Assert.Null(Gpt.Read(new MemoryStream(new byte[1024]), 512));
    }

    [Fact]
    public void Read_FourKibSectors_RoundTrips()
    {
        var disk = new SparseMemoryStream(16_384L * 4096);
        new GptBuilder(16_384, 4096)
            .WithDiskGuid(DiskId)
            .AddPartition(GptTypes.BasicData, 256, 8191, "data")
            .Build()
            .WriteTo(disk);

        var gpt = Gpt.Read(disk, 4096);

        Assert.NotNull(gpt);
        Assert.Equal(6, gpt.FirstUsableLba);
        Assert.Equal(16_378, gpt.LastUsableLba);
        Assert.Equal(new GptPartition(GptTypes.BasicData, gpt.Partitions[0].UniqueGuid, 256, 8191, GptAttributes.None, "data"), gpt.Partitions[0]);
        Assert.Null(Gpt.Read(disk, 512));
    }

    [Fact]
    public void Builder_Rejects_PartitionsOutsideTheUsableRange()
    {
        var builder = new GptBuilder(131_072);

        Assert.Equal(34, builder.FirstUsableLba);
        Assert.Equal(131_038, builder.LastUsableLba);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddPartition(GptTypes.BasicData, 33, 5000));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddPartition(GptTypes.BasicData, 2048, 131_039));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddPartition(GptTypes.BasicData, 5000, 4000));
        builder.AddPartition(GptTypes.BasicData, 34, 131_038);
    }

    [Fact]
    public void Builder_Rejects_OverlapsBadNamesAndEmptyTypes()
    {
        var builder = new GptBuilder(131_072).AddPartition(GptTypes.BasicData, 2048, 4095);

        Assert.Throws<InvalidOperationException>(() => builder.AddPartition(GptTypes.BasicData, 4095, 8000));
        Assert.Throws<ArgumentException>(() => builder.AddPartition(GptTypes.BasicData, 5000, 6000, new string('x', 37)));
        Assert.Throws<ArgumentException>(() => builder.AddPartition(Guid.Empty, 5000, 6000));
        builder.AddPartition(GptTypes.BasicData, 4096, 8000, new string('x', 36));
    }

    [Fact]
    public void Builder_Rejects_ADiskThatIsTooSmall()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GptBuilder(67));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GptBuilder(1000, 520));
        Assert.Equal(34, new GptBuilder(68).LastUsableLba);
    }

    [Fact]
    public void Builder_FillsAllOneHundredTwentyEightSlots()
    {
        var builder = new GptBuilder(1_000_000);
        for (var i = 0; i < 128; i++)
        {
            builder.AddPartition(GptTypes.LinuxData, 2048 + i * 100, 2048 + i * 100 + 99);
        }

        Assert.Throws<InvalidOperationException>(() => builder.AddPartition(GptTypes.LinuxData, 500_000, 500_100));
        Assert.Equal(128, Gpt.Read(Written(builder, 1_000_000), 512)!.Partitions.Count);
    }

    [Fact]
    public void GptTypes_AreDistinctAndWellFormed()
    {
        var all = typeof(GptTypes).GetProperties()
            .Where(p => p.PropertyType == typeof(Guid))
            .Select(p => (Guid)p.GetValue(null)!)
            .Where(g => g != Guid.Empty)
            .ToList();

        Assert.True(all.Count >= 24);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(new Guid("21686148-6449-6E6F-744E-656564454649"), GptTypes.BiosBoot);

        // "Hah!IdontNeedEFI": the BIOS boot GUID spells it in ASCII.
        Assert.Equal("Hah!IdontNeedEFI", Encoding.ASCII.GetString(GptTypes.BiosBoot.ToByteArray()));
    }

    private static SparseMemoryStream Written(out long total)
    {
        total = 131_072;
        var builder = new GptBuilder(total)
            .WithDiskGuid(DiskId)
            .AddPartition(GptTypes.EfiSystem, 2048, 22_527, "EFI")
            .AddPartition(GptTypes.BasicData, 22_528, 100_000, "Data");
        return Written(builder, total);
    }

    private static SparseMemoryStream Written(GptBuilder builder, long total)
    {
        var disk = new SparseMemoryStream(total * 512);
        builder.Build().WriteTo(disk);
        return disk;
    }

    private static byte[] Sector(Stream disk, long lba)
    {
        var buffer = new byte[512];
        disk.Position = lba * 512;
        disk.ReadExactly(buffer);
        return buffer;
    }

    private static uint ReferenceCrc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
