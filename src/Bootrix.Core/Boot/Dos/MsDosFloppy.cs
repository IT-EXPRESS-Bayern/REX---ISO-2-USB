// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using DiscFatFileSystem = DiscUtils.Fat.FatFileSystem;

namespace Bootrix.Core.Boot.Dos;

/// <summary>
/// Turns the Windows ME startup diskette (MS-DOS 8.0) into a <see cref="DosSystem"/>: the system files, the
/// tools for keyboard and code page, and the diskette's own boot sector. The image comes from the user's
/// download of diskcopy.dll and is never stored by Bootrix.
/// </summary>
public static class MsDosFloppy
{
    private static readonly string[] LocaleFiles =
    [
        "MODE.COM", "KEYB.COM", "KEYBOARD.SYS", "KEYBRD2.SYS", "KEYBRD3.SYS", "KEYBRD4.SYS", "DISPLAY.SYS",
        "EGA.CPI", "EGA2.CPI", "EGA3.CPI",
    ];

    // The same two one-byte changes Rufus makes: a conditional jump behind a compare with the drive number (80h in IO.SYS,
    // 03h in COMMAND.COM) becomes unconditional. Without the first, DOS answers "Invalid system disk" when it starts from a
    // hard disk or stick. The bytes around the jump are checked so that only the known files are touched.
    private static readonly PatchSite IoSysPatch = new("IO.SYS", 116_736, 0x3A8, [0xFA, 0x80, 0x75, 0x09, 0x8D, 0xB6, 0x99, 0x00], 2);

    private static readonly PatchSite CommandPatch = new("COMMAND.COM", 93_040, 0x650C, [0x15, 0x80, 0xFA, 0x03, 0x75, 0x10, 0xB8, 0x0E], 4);

    private const byte ShortJump = 0xEB;

    /// <summary>
    /// The system disk for the given diskette image. FAT32 is not supported: only the diskette's FAT12 boot sector is
    /// available, and it reads its volume through CHS, so the BPB has to carry the geometry the BIOS will assume.
    /// </summary>
    public static DosSystem ToSystem(ReadOnlyMemory<byte> floppyImage)
    {
        if (floppyImage.Length != DiskcopyDll.FloppyBytes)
        {
            throw Invalid($"the diskette image has {floppyImage.Length} bytes");
        }

        var image = floppyImage.ToArray();
        byte[] bootSector = image[..512];

        using var stream = new MemoryStream(image, writable: false);
        using var fat = new DiscFatFileSystem(stream);

        var ioSys = Patched(IoSysPatch, Read(fat, "IO.SYS"));
        var commandCom = Patched(CommandPatch, Read(fat, "COMMAND.COM"));

        var files = new List<DosFile>
        {
            new("\\IO.SYS", ioSys, DosFile.SystemFile),
            new("\\MSDOS.SYS", Read(fat, "MSDOS.SYS"), DosFile.SystemFile),
            new("\\COMMAND.COM", commandCom),
            DosFile.Text("\\AUTOEXEC.BAT", "@ECHO OFF\nPATH=.;\\;\\LOCALE\n"),
        };
        files.AddRange(LocaleFiles.Where(name => fat.FileExists(name)).Select(name => new DosFile("\\LOCALE\\" + name, Read(fat, name))));

        return new DosSystem(DosFlavor.MsDos, files, (options, diskSectors) => Customize(options, diskSectors, bootSector));
    }

    private static FatFormatOptions Customize(FatFormatOptions options, long diskSectors, byte[] bootSector)
    {
        if (FatGeometry.Compute(options).Type == FatType.Fat32)
        {
            throw new BootrixException(ErrorCode.FileSystemUnsupported, "MS-DOS boots from FAT12 and FAT16 only")
            {
                Arguments = [FileSystemKind.Fat32, "MS-DOS"],
            };
        }

        var customized = options with { BootCode = bootSector };
        if (options.DriveNumber == 0)
        {
            // A diskette keeps the geometry of its format.
            return customized;
        }

        // The boot sector reads IO.SYS by cylinder, head and sector with the numbers from the BPB. They must be the ones
        // the BIOS translates LBA to, and old BIOSes pick them from the capacity of the disk.
        var geometry = ChsGeometry.ForCapacity(diskSectors);
        return customized with { SectorsPerTrack = geometry.SectorsPerTrack, Heads = geometry.Heads };
    }

    private static byte[] Read(DiscFatFileSystem fat, string name)
    {
        if (!fat.FileExists(name))
        {
            throw Invalid($"{name} is missing from the diskette");
        }

        using var file = fat.OpenFile(name, FileMode.Open, FileAccess.Read);
        var content = new byte[file.Length];
        file.ReadExactly(content);
        return content;
    }

    private static byte[] Patched(PatchSite site, byte[] content)
    {
        if (content.Length != site.Length || !content.AsSpan(site.ExpectedAt, site.Expected.Length).SequenceEqual(site.Expected))
        {
            throw Invalid($"{site.Name} is not the version Bootrix knows how to prepare");
        }

        var patched = (byte[])content.Clone();
        patched[site.ExpectedAt + site.JumpIndex] = ShortJump;
        return patched;
    }

    private static BootrixException Invalid(string detail) =>
        new(ErrorCode.MsDosImageInvalid, detail) { Arguments = [detail] };

    private sealed record PatchSite(string Name, int Length, int ExpectedAt, byte[] Expected, int JumpIndex);
}
