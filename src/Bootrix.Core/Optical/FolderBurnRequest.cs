// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical;

/// <summary>File system layers of a data disc. Values match FsiFileSystems.</summary>
[Flags]
public enum DiscFileSystems
{
    None = 0,
    Iso9660 = 1,
    Joliet = 2,
    Udf = 4,
}

/// <summary>UDF revisions IMAPI2FS can write, as the hexadecimal revision number it expects (0x102 is UDF 1.02).</summary>
public enum UdfRevision
{
    Udf102 = 0x102,
    Udf150 = 0x150,
    Udf200 = 0x200,
    Udf201 = 0x201,
    Udf250 = 0x250,
}

public enum BootPlatform
{
    /// <summary>BIOS, El Torito platform ID 0.</summary>
    Bios = 0x00,

    /// <summary>UEFI, platform ID 0xEF.</summary>
    Efi = 0xEF,
}

public enum BootEmulation
{
    /// <summary>The boot image is loaded and run as it is; what Windows setup and every UEFI image use.</summary>
    None = 0,
    Floppy1200K = 1,
    Floppy1440K = 2,
    Floppy2880K = 3,
    HardDisk = 4,
}

/// <summary>One El Torito entry. IMAPI2FS can write several of them, so a disc can boot on BIOS and UEFI machines.</summary>
public sealed record DiscBootEntry(BootPlatform Platform, string ImagePath, BootEmulation Emulation = BootEmulation.None, string? Manufacturer = null);

public sealed record FolderBurnRequest
{
    public required string SourceFolder { get; init; }

    /// <summary>Null lets the planner choose from the content of the folder.</summary>
    public DiscFileSystems? FileSystems { get; init; }

    public UdfRevision UdfRevision { get; init; } = UdfRevision.Udf102;

    /// <summary>Put the folder itself on the disc instead of only its content.</summary>
    public bool IncludeBaseDirectory { get; init; }

    public IReadOnlyList<DiscBootEntry> BootEntries { get; init; } = [];
}
