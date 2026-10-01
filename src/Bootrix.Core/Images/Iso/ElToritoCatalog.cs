// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Iso;

public enum ElToritoPlatform : byte
{
    X86 = 0x00,
    PowerPc = 0x01,
    Mac = 0x02,
    Efi = 0xEF,
}

public enum ElToritoEmulation : byte
{
    None = 0,
    Floppy1200 = 1,
    Floppy1440 = 2,
    Floppy2880 = 3,
    HardDisk = 4,
}

/// <summary>One bootable (or disabled) image from the El Torito boot catalog.</summary>
/// <param name="Platform">Platform ID of the section (validation entry for the default entry); 0xEF means EFI.</param>
/// <param name="Bootable">The boot indicator is 0x88.</param>
/// <param name="LoadSegment">Real-mode load segment; 0 stands for the conventional 0x7C0.</param>
/// <param name="SystemType">Partition type byte of the image for hard disk emulation.</param>
/// <param name="SectorCount">Number of 512-byte sectors the BIOS loads; frequently 0, 1 or truncated for large EFI images.</param>
/// <param name="ImageSector">First 2048-byte sector of the boot image in the ISO.</param>
/// <param name="SectionId">The section's ID string, if any.</param>
/// <param name="IsDefault">The initial/default entry rather than a section entry.</param>
public sealed record ElToritoEntry(
    byte Platform,
    bool Bootable,
    ElToritoEmulation Emulation,
    ushort LoadSegment,
    byte SystemType,
    ushort SectorCount,
    uint ImageSector,
    string? SectionId,
    bool IsDefault)
{
    public bool IsEfi => Platform == (byte)ElToritoPlatform.Efi;

    public bool IsBios => Platform == (byte)ElToritoPlatform.X86;

    public long ImageOffset => ImageSector * 2048L;
}

public sealed record ElToritoCatalog(
    uint CatalogSector,
    byte ValidationPlatform,
    string? Manufacturer,
    bool ValidationChecksumOk,
    IReadOnlyList<ElToritoEntry> Entries)
{
    public bool HasBios => Entries.Any(entry => entry is { Bootable: true, IsBios: true });

    public bool HasEfi => Entries.Any(entry => entry is { Bootable: true, IsEfi: true });

    public IEnumerable<ElToritoEntry> EfiEntries => Entries.Where(entry => entry is { Bootable: true, IsEfi: true });
}
