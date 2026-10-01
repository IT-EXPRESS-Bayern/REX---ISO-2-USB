// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Images.Iso;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images.Iso;

public sealed partial class ElToritoTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly byte[] EfiLoader = "stub efi loader"u8.ToArray();

    private Dictionary<string, byte[]> Tree(params (string Name, byte[] Content)[] extra)
    {
        var efi = IsoBuilder.FatImage(_dir, "efi.img", 2048, new Dictionary<string, string> { ["EFI/BOOT/BOOTX64.EFI"] = "efi" });
        var files = new Dictionary<string, byte[]>
        {
            ["isolinux/isolinux.bin"] = IsoBuilder.FakeBootLoader(),
            ["boot/efi.img"] = efi,
            ["readme.txt"] = "hello"u8.ToArray(),
        };
        foreach (var (name, content) in extra)
        {
            files[name] = content;
        }

        return files;
    }

    /// <summary>Entries as printed by <c>xorriso -report_el_torito plain</c>.</summary>
    private sealed record ReferenceEntry(string Platform, bool Bootable, string Emulation, int LoadSegment, int SystemType, int LoadSize, long Lba);

    [GeneratedRegex(@"^El Torito boot img :\s+(\d+)\s+(\S+)\s+(\S)\s+(\S+)\s+(0x[0-9a-f]+)\s+(0x[0-9a-f]+)\s+(\d+)\s+(\d+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ReferenceLine();

    private static (long CatalogSector, List<ReferenceEntry> Entries) Reference(string iso)
    {
        var report = ReferenceTool.Run("xorriso", ["-indev", iso, "-report_el_torito", "plain"], null, null, null).StandardOutput;
        var catalog = Regex.Match(report, @"El Torito catalog\s+:\s+(\d+)");
        var entries = ReferenceLine().Matches(report).Select(match => new ReferenceEntry(
            match.Groups[2].Value,
            match.Groups[3].Value == "y",
            match.Groups[4].Value,
            Convert.ToInt32(match.Groups[5].Value, 16),
            Convert.ToInt32(match.Groups[6].Value, 16),
            int.Parse(match.Groups[7].Value, CultureInfo.InvariantCulture),
            long.Parse(match.Groups[8].Value, CultureInfo.InvariantCulture))).ToList();
        return (long.Parse(catalog.Groups[1].Value, CultureInfo.InvariantCulture), entries);
    }

    private static string PlatformName(ElToritoEntry entry) => entry.Platform switch
    {
        0x00 => "BIOS",
        0xEF => "UEFI",
        0x01 => "PPC",
        0x02 => "Mac",
        _ => "?",
    };

    private static string EmulationName(ElToritoEntry entry) => entry.Emulation switch
    {
        ElToritoEmulation.None => "none",
        ElToritoEmulation.Floppy1200 => "fd1.2",
        ElToritoEmulation.Floppy1440 => "fd1.4",
        ElToritoEmulation.Floppy2880 => "fd2.8",
        ElToritoEmulation.HardDisk => "hd",
        _ => "?",
    };

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public void Read_BiosAndEfiEntries_MatchTheXorrisoReport()
    {
        var iso = IsoBuilder.Build(_dir, "dual", Tree(), new IsoOptions
        {
            BootImages = [new BootImageSpec("isolinux/isolinux.bin"), new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)],
        });

        using var stream = File.OpenRead(iso);
        var catalog = ElToritoParser.Read(stream)!;
        var (sector, reference) = Reference(iso);

        Assert.Equal(sector, catalog.CatalogSector);
        Assert.True(catalog.ValidationChecksumOk);
        Assert.Equal(reference.Count, catalog.Entries.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            var expected = reference[i];
            var actual = catalog.Entries[i];
            Assert.Equal(expected.Platform, PlatformName(actual));
            Assert.Equal(expected.Bootable, actual.Bootable);
            Assert.Equal(expected.Emulation, EmulationName(actual));
            Assert.Equal(expected.LoadSegment, actual.LoadSegment);
            Assert.Equal(expected.SystemType, actual.SystemType);
            Assert.Equal(expected.LoadSize, actual.SectorCount);
            Assert.Equal(expected.Lba, actual.ImageSector);
        }

        Assert.True(catalog.HasBios);
        Assert.True(catalog.HasEfi);
        Assert.True(catalog.Entries[0].IsDefault);
        Assert.False(catalog.Entries[1].IsDefault);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public void Read_BiosOnly_HasNoEfiEntry()
    {
        var iso = IsoBuilder.Build(_dir, "bios", Tree(), new IsoOptions { BootImages = [new BootImageSpec("isolinux/isolinux.bin")] });

        using var stream = File.OpenRead(iso);
        var catalog = ElToritoParser.Read(stream)!;

        Assert.True(catalog.HasBios);
        Assert.False(catalog.HasEfi);
        Assert.Single(catalog.Entries);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public void Read_EfiOnly_UsesTheSectionPlatform()
    {
        var iso = IsoBuilder.Build(_dir, "efi", Tree(), new IsoOptions { BootImages = [new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)] });

        using var stream = File.OpenRead(iso);
        var catalog = ElToritoParser.Read(stream)!;
        var (_, reference) = Reference(iso);

        Assert.True(catalog.HasEfi);
        Assert.Equal(reference.Select(e => e.Lba), catalog.Entries.Select(e => (long)e.ImageSector));
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public void Read_FloppyEmulation_ReportsTheMediaType()
    {
        var floppy = IsoBuilder.FatImage(_dir, "floppy.img", 1440, new Dictionary<string, string> { ["IO.SYS"] = "x", ["COMMAND.COM"] = "y" });
        var iso = IsoBuilder.Build(_dir, "floppy", new Dictionary<string, byte[]> { ["boot.img"] = floppy }, new IsoOptions
        {
            BootImages = [new BootImageSpec("boot.img", Emulation: BootEmulation.Floppy)],
        });

        using var stream = File.OpenRead(iso);
        var catalog = ElToritoParser.Read(stream)!;
        var (_, reference) = Reference(iso);

        Assert.Equal(ElToritoEmulation.Floppy1440, catalog.Entries[0].Emulation);
        Assert.Equal(reference[0].Emulation, EmulationName(catalog.Entries[0]));
        Assert.Equal(reference[0].Lba, catalog.Entries[0].ImageSector);
        Assert.Equal(1_474_560, ElToritoParser.ResolveImageLength(stream, catalog.Entries[0]));
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public void ResolveImageLength_ForEfiImage_ComesFromTheFatBootSector()
    {
        var iso = IsoBuilder.Build(_dir, "efi-size", Tree(), new IsoOptions
        {
            BootImages = [new BootImageSpec("isolinux/isolinux.bin"), new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)],
        });

        // Some writers leave the load size of the EFI entry at 1; patch the third record (section entry) to look like that.
        var (catalogSector, _) = Reference(iso);
        using (var file = new FileStream(iso, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = (catalogSector * 2048) + (3 * 32) + 6;
            file.Write([1, 0]);
        }

        using var stream = File.OpenRead(iso);
        var efi = ElToritoParser.Read(stream)!.EfiEntries.Single();

        // A load size of one sector would be useless; the BPB says 2 MiB.
        Assert.Equal(1, efi.SectorCount);
        Assert.Equal(2048 * 1024, ElToritoParser.ResolveImageLength(stream, efi));
    }

    [Fact]
    public void Read_ImageWithoutBootRecord_ReturnsNull()
    {
        using var stream = new MemoryStream(new byte[64 * 1024]);

        Assert.Null(ElToritoParser.Read(stream));
    }

    [Fact]
    public void Parse_HandcraftedCatalogWithSectionsAndExtensions_IsDecoded()
    {
        var data = new byte[2048];
        data[0] = 1;
        data[1] = 0;
        "MANUFACTURER"u8.CopyTo(data.AsSpan(4));
        data[30] = 0x55;
        data[31] = 0xAA;
        var sum = 0;
        for (var i = 0; i < 32; i += 2)
        {
            sum += data[i] | (data[i + 1] << 8);
        }

        var checksum = (ushort)(0x10000 - (sum & 0xFFFF));
        data[28] = (byte)checksum;
        data[29] = (byte)(checksum >> 8);

        // Default entry: bootable, no emulation, load segment 0x07C0, 4 sectors, sector 30.
        data[32] = 0x88;
        data[34] = 0xC0;
        data[35] = 0x07;
        data[38] = 4;
        data[40] = 30;

        // Section header (more follow) for EFI with two entries, the first followed by one extension entry.
        data[64] = 0x90;
        data[65] = 0xEF;
        data[66] = 2;
        "SEC1"u8.CopyTo(data.AsSpan(68));
        data[96] = 0x88;
        data[97] = 0x20;
        data[104] = 40;
        data[128] = 0x44;
        data[160] = 0x00;
        data[168] = 50;

        // Final section header for BIOS with one entry.
        data[192] = 0x91;
        data[193] = 0x00;
        data[194] = 1;
        data[224] = 0x88;
        data[232] = 60;

        var catalog = ElToritoParser.Parse(20, data)!;

        Assert.Equal(20u, catalog.CatalogSector);
        Assert.Equal("MANUFACTURER", catalog.Manufacturer);
        Assert.True(catalog.ValidationChecksumOk);
        Assert.Equal([30u, 40u, 50u, 60u], catalog.Entries.Select(e => e.ImageSector));
        Assert.Equal([true, true, false, true], catalog.Entries.Select(e => e.Bootable));
        Assert.Equal([(byte)0x00, (byte)0xEF, (byte)0xEF, (byte)0x00], catalog.Entries.Select(e => e.Platform));
        Assert.Equal("SEC1", catalog.Entries[1].SectionId);
        Assert.Equal<ushort>(0x07C0, catalog.Entries[0].LoadSegment);
        Assert.Equal<ushort>(4, catalog.Entries[0].SectorCount);
    }

    [Fact]
    public void Parse_CorruptChecksum_IsReported()
    {
        var data = new byte[2048];
        data[0] = 1;
        data[30] = 0x55;
        data[31] = 0xAA;
        data[32] = 0x88;

        Assert.False(ElToritoParser.Parse(1, data)!.ValidationChecksumOk);
    }

    [Fact]
    public void Parse_WithoutValidationEntry_ReturnsNull()
    {
        Assert.Null(ElToritoParser.Parse(1, new byte[2048]));
    }
}
