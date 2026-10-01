// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// Turns the reserved area of a FAT32 volume that Windows has formatted into boot code for Bootrix's own
/// formatter. The BPB (sizes, hidden sectors, geometry, serial) stays Bootrix's; only the code is taken, so
/// it works for volumes of any size, including those above 32 GB that Windows itself refuses to format.
/// </summary>
public static class WindowsFatBootCode
{
    private const int SectorBytes = 512;
    private const int FsInfoSector = 1;
    private const int BackupFirst = 6;
    private const int BackupLast = 8;
    private const int Fat32TypeOffset = 0x52;
    private const int Fat32CodeOffset = 0x5A;
    private const int ReservedOffset = 14;

    /// <summary>The loader's name as the boot code must carry it. NT 5 boot code looks for NTLDR instead and cannot start Windows setup.</summary>
    private static readonly byte[] LoaderName = "BOOTMGR"u8.ToArray();

    /// <param name="reservedArea">The first sectors of the volume, at least up to the end of the reserved area or 32 sectors, whichever is less.</param>
    /// <exception cref="BootrixException">The data is no FAT32 boot area, or its code does not load BOOTMGR.</exception>
    public static FatBootSectors FromReservedArea(ReadOnlySpan<byte> reservedArea)
    {
        if (reservedArea.Length < 3 * SectorBytes || reservedArea.Length % SectorBytes != 0)
        {
            throw Unavailable($"{reservedArea.Length} bytes are not a whole reserved area");
        }

        var boot = reservedArea[..SectorBytes];
        if (boot[510] != 0x55 || boot[511] != 0xAA || boot[0] is not (0xEB or 0xE9))
        {
            throw Unavailable("sector 0 is not a boot sector");
        }

        if (!boot.Slice(Fat32TypeOffset, 8).SequenceEqual("FAT32   "u8)
            || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]) != SectorBytes)
        {
            throw Unavailable("the volume is not FAT32 with 512-byte sectors");
        }

        var available = reservedArea.Length / SectorBytes;
        var reserved = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(boot[ReservedOffset..]);
        var last = LastCodeSector(reservedArea, Math.Min(available, reserved));
        if (boot[Fat32CodeOffset..(SectorBytes - 2)].IndexOfAnyExcept((byte)0) < 0)
        {
            throw Unavailable("the boot sector holds no code");
        }

        var sectors = reservedArea[..((last + 1) * SectorBytes)].ToArray();

        // Bootrix writes its own FSInfo; whatever Windows put there describes a volume that does not exist here.
        sectors.AsSpan(FsInfoSector * SectorBytes, SectorBytes).Clear();

        if (sectors.AsSpan().IndexOf(LoaderName) < 0)
        {
            throw Unavailable("the boot code does not load BOOTMGR (no loader name in it)");
        }

        return new FatBootSectors(sectors);
    }

    /// <summary>The boot code becomes the formatter's <see cref="FatFormatOptions.BootCode"/>; everything else about the volume stays as planned.</summary>
    public static FatFormatOptions Apply(FatFormatOptions options, FatBootSectors code)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(code);
        return options with { BootCode = code.Sectors };
    }

    /// <summary>
    /// Sectors 0 and 2 always carry code. Beyond them the NT boot code keeps more of itself in the reserved area
    /// (sector 12); the last sector with anything in it marks the end, leaving out FSInfo and the backup copy of
    /// the first three sectors, which Bootrix recreates.
    /// </summary>
    private static int LastCodeSector(ReadOnlySpan<byte> area, int sectors)
    {
        var last = 2;
        for (var sector = 3; sector < sectors; sector++)
        {
            if (sector is >= BackupFirst and <= BackupLast)
            {
                continue;
            }

            if (area.Slice(sector * SectorBytes, SectorBytes).IndexOfAnyExcept((byte)0) >= 0)
            {
                last = sector;
            }
        }

        return last;
    }

    private static BootrixException Unavailable(string detail) =>
        new(ErrorCode.BootCodeUnavailable, detail) { Arguments = [detail] };
}
