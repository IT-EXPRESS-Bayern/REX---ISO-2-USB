// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.FileSystems.Fat;

public class FatFormatterTests
{
    private const long Mib = 1024 * 1024;
    private const long Gib = 1024 * Mib;
    private const long Tib = 1024 * Gib;

    private static byte[] Sector(Stream stream, long index, int bytesPerSector = 512)
    {
        var buffer = new byte[bytesPerSector];
        stream.Position = index * bytesPerSector;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static (SparseMemoryStream Stream, FatFormatResult Result) Format(FatFormatOptions options)
    {
        var stream = new SparseMemoryStream(options.TotalBytes);
        return (stream, FatFormatter.Format(stream, options));
    }

    [Fact]
    public void Fat16BootSector_HasTheBpbFieldsAtTheirOffsets()
    {
        var (stream, result) = Format(new FatFormatOptions
        {
            TotalBytes = 100 * Mib,
            Type = FatType.Fat16,
            Label = "data",
            VolumeId = 0xA1B2C3D4,
            HiddenSectors = 2048,
            SectorsPerTrack = 32,
            Heads = 64,
            DriveNumber = 0x80,
        });

        var boot = Sector(stream, 0);
        Assert.Equal([0xEB, 0x3C, 0x90], boot[..3]);
        Assert.Equal("MSWIN4.1", Encoding.ASCII.GetString(boot, 3, 8));
        Assert.Equal(512, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)));
        Assert.Equal(result.Layout.SectorsPerCluster, boot[13]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14)));
        Assert.Equal(2, boot[16]);
        Assert.Equal(512, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19)));
        Assert.Equal(0xF8, boot[21]);
        Assert.Equal(result.Layout.SectorsPerFat, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(24)));
        Assert.Equal(64, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(26)));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(28)));
        Assert.Equal(result.Layout.TotalSectors, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32)));
        Assert.Equal(0x80, boot[36]);
        Assert.Equal(0x29, boot[38]);
        Assert.Equal(0xA1B2C3D4u, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(39)));
        Assert.Equal("DATA       ", Encoding.ASCII.GetString(boot, 43, 11));
        Assert.Equal("FAT16   ", Encoding.ASCII.GetString(boot, 54, 8));
        Assert.Equal([0x55, 0xAA], boot[510..]);
    }

    [Fact]
    public void Fat12BootSector_UsesTheSixteenBitTotalWhenItFits()
    {
        var (stream, result) = Format(FloppyPreset.All.Single(p => p.Name == "1.44M").ToOptions());

        var boot = Sector(stream, 0);

        Assert.Equal(2880, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32)));
        Assert.Equal(0xF0, boot[21]);
        Assert.Equal(0x00, boot[36]);
        Assert.Equal("FAT12   ", Encoding.ASCII.GetString(boot, 54, 8));
        Assert.Equal(224, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17)));
        Assert.Equal(FatType.Fat12, result.Layout.Type);
    }

    [Fact]
    public void Fat32BootSector_HasTheExtendedFieldsAtTheirOffsets()
    {
        var (stream, result) = Format(new FatFormatOptions
        {
            TotalBytes = 1 * Gib,
            Type = FatType.Fat32,
            Label = "BOOT",
            VolumeId = 0x01020304,
        });

        var boot = Sector(stream, 0);
        Assert.Equal([0xEB, 0x58, 0x90], boot[..3]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22)));
        Assert.Equal(result.Layout.SectorsPerFat, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(40)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(42)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(48)));
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(50)));
        Assert.Equal(0x80, boot[64]);
        Assert.Equal(0x29, boot[66]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(67)));
        Assert.Equal("BOOT       ", Encoding.ASCII.GetString(boot, 71, 11));
        Assert.Equal("FAT32   ", Encoding.ASCII.GetString(boot, 82, 8));
        Assert.Equal([0x55, 0xAA], boot[510..]);
    }

    [Fact]
    public void Fat32_FsInfoAndBackupSectorsAreWritten()
    {
        var (stream, result) = Format(new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat32 });

        var fsInfo = Sector(stream, 1);
        Assert.Equal(0x41615252u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo));
        Assert.Equal(0x61417272u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(0x1E4)));
        Assert.Equal((uint)(result.Layout.ClusterCount - 1), BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(0x1E8)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(0x1EC)));
        Assert.Equal(0xAA550000u, BinaryPrimitives.ReadUInt32LittleEndian(fsInfo.AsSpan(0x1FC)));

        Assert.Equal(Sector(stream, 0), Sector(stream, 6));
        Assert.Equal(fsInfo, Sector(stream, 7));
        Assert.Equal(Sector(stream, 2), Sector(stream, 8));
        Assert.Equal([0x55, 0xAA], Sector(stream, 2)[510..]);
    }

    [Fact]
    public void FourKibSectors_CarryTheBootSignatureAtBothEnds()
    {
        var (stream, _) = Format(new FatFormatOptions { TotalBytes = 1 * Gib, BytesPerSector = 4096, Type = FatType.Fat32 });

        var boot = Sector(stream, 0, 4096);

        Assert.Equal(4096, BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11)));
        Assert.Equal([0x55, 0xAA], boot[510..512]);
        Assert.Equal([0x55, 0xAA], boot[^2..]);
        Assert.Equal(Sector(stream, 0, 4096), Sector(stream, 6, 4096));
    }

    [Fact]
    public void FatTables_StartWithMediaDescriptorAndEndOfChainMarkers()
    {
        var (fat12, r12) = Format(FloppyPreset.All.Single(p => p.Name == "720K").ToOptions());
        var (fat16, r16) = Format(new FatFormatOptions { TotalBytes = 20 * Mib, Type = FatType.Fat16 });
        var (fat32, r32) = Format(new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat32, MediaDescriptor = 0xF8 });

        Assert.Equal([0xF9, 0xFF, 0xFF, 0x00], Sector(fat12, r12.Layout.ReservedSectors)[..4]);
        Assert.Equal([0xF8, 0xFF, 0xFF, 0xFF, 0x00], Sector(fat16, r16.Layout.ReservedSectors)[..5]);
        Assert.Equal(
            [0xF8, 0xFF, 0xFF, 0x0F, 0xFF, 0xFF, 0xFF, 0x0F, 0xFF, 0xFF, 0xFF, 0x0F, 0x00],
            Sector(fat32, r32.Layout.ReservedSectors)[..13]);

        // The second copy is identical to the first.
        var second = r32.Layout.ReservedSectors + r32.Layout.SectorsPerFat;
        Assert.Equal(Sector(fat32, r32.Layout.ReservedSectors), Sector(fat32, second));
    }

    [Fact]
    public void Label_IsWrittenToTheRootDirectoryAsAVolumeLabelEntry()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 14, 30, 45, TimeSpan.Zero));
        var (stream, result) = Format(new FatFormatOptions
        {
            TotalBytes = 20 * Mib,
            Type = FatType.Fat16,
            Label = "Mein Stick",
            TimeProvider = clock,
        });

        var entry = Sector(stream, result.Layout.RootDirectoryStartSector)[..32];

        Assert.Equal("MEIN STICK ", Encoding.ASCII.GetString(entry, 0, 11));
        Assert.Equal(0x08, entry[11]);
        Assert.Equal((2026 - 1980) << 9 | 10 << 5 | 1, BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(24)));
        Assert.Equal(14 << 11 | 30 << 5 | 22, BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(22)));
        Assert.Equal("MEIN STICK", result.Label);
        Assert.All(Sector(stream, result.Layout.RootDirectoryStartSector)[32..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Label_OnFat32_LivesInTheFirstSectorOfTheRootCluster()
    {
        var (stream, result) = Format(new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat32, Label = "ROOT" });

        var entry = Sector(stream, result.Layout.DataStartSector)[..32];

        Assert.Equal("ROOT       ", Encoding.ASCII.GetString(entry, 0, 11));
        Assert.Equal(0x08, entry[11]);
    }

    [Fact]
    public void WithoutLabel_BootSectorSaysNoNameAndRootStaysEmpty()
    {
        var (stream, result) = Format(new FatFormatOptions { TotalBytes = 20 * Mib, Type = FatType.Fat16 });

        Assert.Equal("NO NAME    ", Encoding.ASCII.GetString(Sector(stream, 0), 43, 11));
        Assert.All(Sector(stream, result.Layout.RootDirectoryStartSector), b => Assert.Equal(0, b));
        Assert.Equal("", result.Label);
    }

    [Fact]
    public void VolumeId_DefaultsToTheClockAndCanBePinned()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 14, 30, 45, 120, TimeSpan.Zero));

        var (_, generated) = Format(new FatFormatOptions { TotalBytes = 20 * Mib, TimeProvider = clock });
        var (_, pinned) = Format(new FatFormatOptions { TotalBytes = 20 * Mib, TimeProvider = clock, VolumeId = 7 });

        Assert.Equal(0x1608370Du, generated.VolumeId);
        Assert.Equal(7u, pinned.VolumeId);
    }

    [Fact]
    public void VolumeId_FromTime_FollowsTheWindowsAlgorithm()
    {
        Assert.Equal(0x1608370Du, FatVolumeId.FromTime(new DateTimeOffset(2026, 10, 1, 14, 30, 45, 120, TimeSpan.Zero)));
        Assert.Equal(0x07E90101u, FatVolumeId.FromTime(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("hello", "HELLO")]
    [InlineData("Mein Stick", "MEIN STICK")]
    [InlineData("a*b?c.d", "A_B_C_D")]
    [InlineData("exactly11chars-and-more", "EXACTLY11CH")]
    [InlineData("trailing   ", "TRAILING")]
    [InlineData("ÄÖÜ", "ÄÖÜ")]
    [InlineData("ä-ß", "Ä-ß")]
    [InlineData("日本語", "___")]
    [InlineData("tab\there", "TAB_HERE")]
    [InlineData("a|b\\c/d", "A_B_C_D")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Label_Normalize_FoldsToUpperCaseOemText(string? input, string expected)
    {
        Assert.Equal(expected, FatLabel.Normalize(input));
    }

    [Fact]
    public void Label_ToField_PadsWithSpacesAndUsesCodePage437()
    {
        var field = FatLabel.ToField("ÄB");

        Assert.Equal(11, field.Length);
        Assert.Equal([0x8E, (byte)'B'], field[..2]);
        Assert.All(field[2..], b => Assert.Equal((byte)' ', b));
    }

    [Fact]
    public void AssumeZeroed_OnAHugeVolume_WritesOnlyTheMetadata()
    {
        const long bytes = 2 * Tib - 512;
        var stream = new SparseMemoryStream(bytes);

        var result = FatFormatter.Format(stream, new FatFormatOptions { TotalBytes = bytes, Label = "HUGE", AssumeZeroed = true });

        Assert.Equal(FatType.Fat32, result.Layout.Type);
        Assert.True(stream.BytesWritten < 1024 * 1024, $"wrote {stream.BytesWritten} bytes");
        Assert.True(stream.PagesAllocated < 40);
    }

    [Fact]
    public void Format_WithoutAssumeZeroed_ClearsReservedAreaFatsAndRoot()
    {
        const long bytes = 64 * Mib;
        var stream = new SparseMemoryStream(bytes);
        stream.Position = 0;
        stream.Write(Enumerable.Repeat((byte)0xE5, 8 * (int)Mib).ToArray());

        var result = FatFormatter.Format(stream, new FatFormatOptions { TotalBytes = bytes, Type = FatType.Fat32 });

        var fatTail = Sector(stream, result.Layout.ReservedSectors + 1);
        Assert.All(fatTail, b => Assert.Equal(0, b));
        Assert.All(Sector(stream, result.Layout.ReservedSectors + result.Layout.SectorsPerFat + 3), b => Assert.Equal(0, b));
        Assert.All(Sector(stream, result.Layout.DataStartSector), b => Assert.Equal(0, b));
        Assert.All(Sector(stream, 3), b => Assert.Equal(0, b));
        Assert.Equal(0xE5, Sector(stream, result.Layout.DataStartSector + result.Layout.SectorsPerCluster)[0]);
    }

    [Fact]
    public void Format_CanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stream = new SparseMemoryStream(1 * Gib);

        Assert.Throws<OperationCanceledException>(() =>
            FatFormatter.Format(stream, new FatFormatOptions { TotalBytes = 1 * Gib }, cts.Token));
    }

    [Fact]
    public void Format_TargetShorterThanTheVolume_IsRefused()
    {
        var stream = new SparseMemoryStream(10 * Mib);

        var ex = Assert.Throws<BootrixException>(() =>
            FatFormatter.Format(stream, new FatFormatOptions { TotalBytes = 20 * Mib }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
        Assert.Equal(0, stream.BytesWritten);
    }

    [Fact]
    public void Format_UnseekableTarget_IsRefused()
    {
        using var stream = new NonSeekableStream();

        Assert.Throws<ArgumentException>(() => FatFormatter.Format(stream, new FatFormatOptions { TotalBytes = 20 * Mib }));
    }

    [Fact]
    public void BootCode_ReplacesCodeAndJumpButKeepsOurBpb()
    {
        var code = new byte[512];
        Array.Fill(code, (byte)0xCC);
        code[0] = 0xEB;
        code[1] = 0x3C;
        code[2] = 0x90;
        Array.Fill(code, (byte)0x11, 3, 0x3E - 3);
        code[0x3E] = 0xB8;
        code[0x1FD] = 0xE9;

        var (stream, _) = Format(new FatFormatOptions
        {
            TotalBytes = 100 * Mib,
            Type = FatType.Fat16,
            BootCode = code,
            OemName = "TESTOEM",
            VolumeId = 0x11223344,
        });

        var boot = Sector(stream, 0);
        Assert.Equal(0xB8, boot[0x3E]);
        Assert.Equal(0xCC, boot[0x100]);
        Assert.Equal(0xE9, boot[0x1FD]);
        Assert.Equal("TESTOEM ", Encoding.ASCII.GetString(boot, 3, 8));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(39)));
        Assert.Equal([0x55, 0xAA], boot[510..]);
    }

    [Fact]
    public void BootCode_OnFat32_ReplacesTheCodeAfterTheExtendedFields()
    {
        var code = new byte[512];
        Array.Fill(code, (byte)0xCC);
        code[0] = 0xEB;
        code[1] = 0x58;
        code[2] = 0x90;
        code[0x5A] = 0xFA;

        var (stream, _) = Format(new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat32, BootCode = code });

        var boot = Sector(stream, 0);
        Assert.Equal(0xFA, boot[0x5A]);
        Assert.Equal(0xCC, boot[0x1FD]);
        Assert.Equal("FAT32   ", Encoding.ASCII.GetString(boot, 82, 8));
        Assert.Equal(boot, Sector(stream, 6));
    }

    [Fact]
    public void BootCode_WithThreeSectors_FillsTheFirstReservedSectorsAndTheirBackup()
    {
        var code = new byte[3 * 512];
        for (var i = 0; i < code.Length; i++)
        {
            code[i] = (byte)(0x40 + i / 512);
        }

        var (stream, result) = Format(new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat32, BootCode = code });

        var sector1 = Sector(stream, 1);
        Assert.Equal(0x41, sector1[0x10]);
        Assert.Equal(0x41615252u, BinaryPrimitives.ReadUInt32LittleEndian(sector1));
        Assert.Equal((uint)(result.Layout.ClusterCount - 1), BinaryPrimitives.ReadUInt32LittleEndian(sector1.AsSpan(0x1E8)));
        var sector2 = Sector(stream, 2);
        Assert.Equal(0x42, sector2[0x10]);
        Assert.Equal([0x55, 0xAA], sector2[510..]);
        Assert.Equal(Sector(stream, 0), Sector(stream, 6));
        Assert.Equal(sector1, Sector(stream, 7));
        Assert.Equal(sector2, Sector(stream, 8));
    }

    [Fact]
    public void BootCode_LargerThanTheFirstSector_IsRejectedOnFat16()
    {
        var options = new FatFormatOptions { TotalBytes = 100 * Mib, Type = FatType.Fat16, BootCode = new byte[1024] };

        Assert.Throws<BootrixException>(() => Format(options));
    }

    [Fact]
    public void BootCode_OfAnOddLength_IsRejected()
    {
        var options = new FatFormatOptions { TotalBytes = 1 * Gib, Type = FatType.Fat32, BootCode = new byte[700] };

        Assert.Throws<BootrixException>(() => Format(options));
    }

    [Fact]
    public void DefaultBootCode_PrintsAMessageThroughTheBios()
    {
        var (stream, _) = Format(new FatFormatOptions { TotalBytes = 100 * Mib, Type = FatType.Fat16 });

        var boot = Sector(stream, 0);
        var text = Encoding.ASCII.GetString(boot, 0x3E, 120);

        Assert.Equal(0xFA, boot[0x3E]);
        Assert.Contains("This disk is not bootable", text, StringComparison.Ordinal);
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }
}
