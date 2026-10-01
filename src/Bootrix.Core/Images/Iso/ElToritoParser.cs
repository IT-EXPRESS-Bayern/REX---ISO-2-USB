// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Images.Iso;

/// <summary>
/// Parser for the El Torito boot catalog (El Torito Bootable CD-ROM Format Specification 1.0, UEFI 2.10
/// section 13.3.2.1 for platform ID 0xEF). DiscUtils only exposes the default entry, which is not enough to
/// find the EFI image of a hybrid ISO.
/// </summary>
public static class ElToritoParser
{
    private const int EntrySize = 32;
    private const int MaxCatalogSectors = 4;
    private const int SectorSize = 2048;

    private const byte BootableIndicator = 0x88;
    private const byte SectionHeader = 0x90;
    private const byte FinalSectionHeader = 0x91;
    private const byte ExtensionIndicator = 0x44;
    private const byte ContinuationBit = 0x20;

    /// <summary>Reads the catalog the boot record points to, or null when the image has no valid one.</summary>
    public static ElToritoCatalog? Read(Stream iso)
    {
        ArgumentNullException.ThrowIfNull(iso);
        var volume = Iso9660Reader.Read(iso);
        return volume?.BootCatalogSector is { } sector ? ReadCatalog(iso, sector) : null;
    }

    public static ElToritoCatalog? ReadCatalog(Stream iso, uint catalogSector)
    {
        var offset = catalogSector * (long)SectorSize;
        if (offset >= iso.Length)
        {
            return null;
        }

        var data = new byte[MaxCatalogSectors * SectorSize];
        iso.Position = offset;
        var read = iso.ReadAtLeast(data, data.Length, throwOnEndOfStream: false);
        return Parse(catalogSector, data.AsSpan(0, read));
    }

    public static ElToritoCatalog? Parse(uint catalogSector, ReadOnlySpan<byte> data)
    {
        // Header ID 1 starts the validation entry; the default entry follows immediately.
        if (data.Length < 2 * EntrySize || data[0] != 0x01)
        {
            return null;
        }

        var validationPlatform = data[1];
        var entries = new List<ElToritoEntry>
        {
            ReadEntry(data.Slice(EntrySize, EntrySize), validationPlatform, null, isDefault: true),
        };

        var position = 2 * EntrySize;
        while (position + EntrySize <= data.Length && data[position] is SectionHeader or FinalSectionHeader)
        {
            var header = data.Slice(position, EntrySize);
            var platform = header[1];
            var count = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
            var sectionId = ReadString(header[4..]);
            position += EntrySize;

            for (var i = 0; i < count && position + EntrySize <= data.Length; i++)
            {
                var entry = data.Slice(position, EntrySize);
                entries.Add(ReadEntry(entry, platform, sectionId, isDefault: false));
                position += EntrySize;
                position = SkipExtensions(data, position, entry[1]);
            }

            if (header[0] == FinalSectionHeader)
            {
                break;
            }
        }

        return new ElToritoCatalog(
            catalogSector,
            validationPlatform,
            ReadString(data.Slice(4, 24)),
            ChecksumIsValid(data[..EntrySize]),
            entries);
    }

    private static ElToritoEntry ReadEntry(ReadOnlySpan<byte> entry, byte platform, string? sectionId, bool isDefault) =>
        new(
            platform,
            entry[0] == BootableIndicator,
            (ElToritoEmulation)(entry[1] & 0x0F),
            BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]),
            entry[4],
            BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
            sectionId,
            isDefault);

    /// <summary>A section entry whose media byte has the continuation bit is followed by extension entries (0x44).</summary>
    private static int SkipExtensions(ReadOnlySpan<byte> data, int position, byte mediaByte)
    {
        var more = (mediaByte & ContinuationBit) != 0;
        while (more && position + EntrySize <= data.Length && data[position] == ExtensionIndicator)
        {
            more = (data[position + 1] & ContinuationBit) != 0;
            position += EntrySize;
        }

        return position;
    }

    /// <summary>The 16 little-endian words of the validation entry must add up to zero modulo 65536.</summary>
    private static bool ChecksumIsValid(ReadOnlySpan<byte> validation)
    {
        var sum = 0;
        for (var i = 0; i < EntrySize; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(validation[i..]);
        }

        return (sum & 0xFFFF) == 0 && validation[30] == 0x55 && validation[31] == 0xAA;
    }

    private static string? ReadString(ReadOnlySpan<byte> field)
    {
        var text = Encoding.ASCII.GetString(field).TrimEnd('\0', ' ');
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Size of the boot image in bytes. The sector count of an entry is a 16-bit number of 512-byte sectors, so
    /// it cannot describe an EFI system partition above 32 MiB and many writers store 0 or 1; the FAT boot sector
    /// is therefore consulted first. Floppy emulation has fixed sizes.
    /// </summary>
    public static long ResolveImageLength(Stream iso, ElToritoEntry entry)
    {
        var remaining = Math.Max(0, iso.Length - entry.ImageOffset);
        long length = entry.Emulation switch
        {
            ElToritoEmulation.Floppy1200 => 1_228_800,
            ElToritoEmulation.Floppy1440 => 1_474_560,
            ElToritoEmulation.Floppy2880 => 2_949_120,
            _ => 0,
        };

        if (length == 0)
        {
            var sector = new byte[512];
            iso.Position = entry.ImageOffset;
            if (iso.ReadAtLeast(sector, sector.Length, throwOnEndOfStream: false) == sector.Length)
            {
                length = Disk.FatBootSector.VolumeBytes(sector);
            }
        }

        if (length == 0)
        {
            length = Math.Max(entry.SectorCount, (ushort)1) * 512L;
        }

        return Math.Min(length, remaining);
    }
}
