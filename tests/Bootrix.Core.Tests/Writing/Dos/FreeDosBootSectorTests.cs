// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Tests.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Writing.Dos;

public class FreeDosBootSectorTests
{
    private const long Mib = 1024 * 1024;
    private static readonly byte[] KernelName = Encoding.ASCII.GetBytes("KERNEL  SYS");

    [Theory]
    [InlineData(FatType.Fat12)]
    [InlineData(FatType.Fat16)]
    [InlineData(FatType.Fat32)]
    public void CodeFor_IsOneSectorThatLoadsKernelSys(FatType type)
    {
        var code = FreeDosBootSector.CodeFor(type);

        Assert.Equal(512, code.Length);
        Assert.Equal([0x55, 0xAA], code[510..]);
        Assert.Equal(0xEB, code[0]);
        Assert.Equal(type == FatType.Fat32 ? 0x58 : 0x3C, code[1]);
        Assert.NotEqual(-1, code.AsSpan().IndexOf(KernelName));
    }

    [Fact]
    public void CodeFor_ReturnsACopyEachTime()
    {
        var first = FreeDosBootSector.CodeFor(FatType.Fat16);
        var original = first[100];
        first[100] ^= 0xFF;

        Assert.Equal(original, FreeDosBootSector.CodeFor(FatType.Fat16)[100]);
    }

    [Theory]
    [InlineData(1, FatType.Fat12)]
    [InlineData(64, FatType.Fat16)]
    [InlineData(600, FatType.Fat32)]
    public void Apply_PicksTheLoaderOfTheTypeTheFormatterProduces(int megabytes, FatType expected)
    {
        var options = FreeDosBootSector.Apply(new FatFormatOptions { TotalBytes = megabytes * Mib });

        Assert.Equal(FreeDosBootSector.CodeFor(expected), options.BootCode);
        Assert.Equal(expected, FatGeometry.Compute(options).Type);
    }

    [Fact]
    public void Apply_KeepsTheOtherOptions()
    {
        var options = FreeDosBootSector.Apply(new FatFormatOptions { TotalBytes = 64 * Mib, HiddenSectors = 2048, Label = "DOS" });

        Assert.Equal(2048u, options.HiddenSectors);
        Assert.Equal("DOS", options.Label);
    }

    [Fact]
    public void Apply_RefusesSectorsOfAnotherSize()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            FreeDosBootSector.Apply(new FatFormatOptions { TotalBytes = 128 * Mib, BytesPerSector = 4096 }));

        Assert.Equal(ErrorCode.SectorSizeUnsupported, ex.Code);
    }

    [Theory]
    [InlineData(8, 12)]
    [InlineData(64, 16)]
    [InlineData(600, 32)]
    public void FormattedVolume_HasTheLoaderCodeAndTheBpbOfTheFormatter(int megabytes, int type)
    {
        var options = FreeDosBootSector.Apply(new FatFormatOptions
        {
            TotalBytes = megabytes * Mib,
            Type = (FatType)type,
            HiddenSectors = 2048,
            SectorsPerTrack = 32,
            Heads = 64,
            DriveNumber = 0x80,
            AssumeZeroed = true,
        });
        var stream = new SparseMemoryStream(megabytes * Mib);

        var result = FatFormatter.Format(stream, options);

        var sector = new byte[512];
        stream.Position = 0;
        stream.ReadExactly(sector);
        var code = FreeDosBootSector.CodeFor((FatType)type);
        var codeStart = type == 32 ? 0x5A : 0x3E;
        Assert.Equal(code[..3], sector[..3]);
        Assert.Equal(code[codeStart..510], sector[codeStart..510]);
        Assert.Equal([0x55, 0xAA], sector[510..]);
        Assert.Equal(512, BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0x0B)));
        Assert.Equal(result.Layout.SectorsPerCluster, sector[0x0D]);
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0x18)));
        Assert.Equal(64, BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(0x1A)));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x1C)));
        Assert.Equal(0x80, sector[type == 32 ? 0x40 : 0x24]);
    }

    [Fact]
    public void Fat32Volume_CarriesFsInfoAndACopyOfTheBootSectorsInSectorSix()
    {
        var options = FreeDosBootSector.Apply(new FatFormatOptions { TotalBytes = 600 * Mib, Type = FatType.Fat32, AssumeZeroed = true });
        var stream = new SparseMemoryStream(600 * Mib);
        var result = FatFormatter.Format(stream, options);

        var area = new byte[9 * 512];
        stream.Position = 0;
        stream.ReadExactly(area);
        ReadOnlySpan<byte> Sector(int index) => area.AsSpan(index * 512, 512);

        Assert.Equal(0x41615252u, BinaryPrimitives.ReadUInt32LittleEndian(Sector(1)));
        Assert.Equal(0x61417272u, BinaryPrimitives.ReadUInt32LittleEndian(Sector(1)[0x1E4..]));
        Assert.Equal((uint)(result.Layout.ClusterCount - 1), BinaryPrimitives.ReadUInt32LittleEndian(Sector(1)[0x1E8..]));
        Assert.Equal(0xAA550000u, BinaryPrimitives.ReadUInt32LittleEndian(Sector(1)[0x1FC..]));
        Assert.Equal([0x55, 0xAA], Sector(2)[510..].ToArray());
        Assert.Equal(6, BinaryPrimitives.ReadUInt16LittleEndian(Sector(0)[0x32..]));
        for (var i = 0; i < 3; i++)
        {
            Assert.True(Sector(i).SequenceEqual(Sector(6 + i)), $"sector {6 + i} differs from sector {i}");
        }
    }

    [RequiresToolFact("fsck.vfat")]
    public void Fat32Volume_WithTheFreeDosLoader_IsCleanAccordingToFsck()
    {
        using var image = new TempImage(600 * Mib);
        using (var stream = image.Open())
        {
            FatFormatter.Format(stream, FreeDosBootSector.Apply(new FatFormatOptions { TotalBytes = 600 * Mib, Type = FatType.Fat32, AssumeZeroed = true }));
        }

        _ = FatVerifier.Fsck(image.Path);
    }
}
