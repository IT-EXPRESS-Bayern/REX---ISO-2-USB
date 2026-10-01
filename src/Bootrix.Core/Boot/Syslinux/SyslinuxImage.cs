// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Boot.Syslinux;

/// <summary>
/// The byte-level work of a Syslinux installation, ported from libinstaller/syslxmod.c, fs.c and setadv.c of the
/// official 6.03 sources (GPL-2.0-or-later). Nothing here touches a device: it turns the released ldlinux.sys and
/// ldlinux.bss plus the sector map of the file on the volume into the bytes that have to be written.
/// </summary>
internal static class SyslinuxImage
{
    public const int SectorSize = 512;
    public const int AdvSectors = 2;

    private const uint LdlinuxMagic = 0x3eb202fe;
    private const uint AdvMagicHead = 0x5a2d2fa5;
    private const uint AdvMagicChecksum = 0xa3041767;
    private const uint AdvMagicTail = 0xdd28bf64;

    // ldlinux.sys is loaded at 0x8000 and a single extent must not cross a 64 KiB boundary of that address.
    private const uint LoadAddress = 0x8000;
    private const int ExtentBytes = 10;

    private const int FatHeadLength = 11;
    private const int FatCodeStart = 90;
    private const int FatCodeEnd = 510;

    /// <summary>The core padded to whole sectors (the installer's bin2c step does this); this length is what the patch checksum covers.</summary>
    public static int PaddedLength(int coreLength) => (coreLength + SectorSize - 1) / SectorSize * SectorSize;

    /// <summary>Sectors that ldlinux.sys occupies on disk: the padded core plus the two ADV sectors.</summary>
    public static int FileSectors(int coreLength) => (PaddedLength(coreLength) / SectorSize) + AdvSectors;

    /// <summary>
    /// The content of a new ldlinux.sys file: the padded core followed by a reset ADV (the auxiliary data vector
    /// that "boot once" and menu-save write to). The patch is applied later, when the sector map is known.
    /// </summary>
    public static byte[] CreateFile(ReadOnlySpan<byte> core)
    {
        var file = new byte[FileSectors(core.Length) * SectorSize];
        core.CopyTo(file);
        ResetAdv(file.AsSpan(PaddedLength(core.Length)));
        return file;
    }

    /// <summary>Fills 2 × 512 bytes with an empty ADV in both copies, with the checksum the loader verifies.</summary>
    public static void ResetAdv(Span<byte> adv)
    {
        adv[..(AdvSectors * SectorSize)].Clear();
        var first = adv[..SectorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(first, AdvMagicHead);
        var checksum = AdvMagicChecksum;
        for (var offset = 8; offset < SectorSize - 4; offset += 4)
        {
            checksum -= BinaryPrimitives.ReadUInt32LittleEndian(first[offset..]);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(first[4..], checksum);
        BinaryPrimitives.WriteUInt32LittleEndian(first[(SectorSize - 4)..], AdvMagicTail);
        first.CopyTo(adv[SectorSize..]);
    }

    /// <summary>
    /// Writes the sector map into a copy of the core and of the boot sector template.
    /// <paramref name="fileSectors"/> are the volume sector numbers of ldlinux.sys in file order, ADV sectors last.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is split into more pieces than the loader has room for.</exception>
    public static PatchedSyslinux Patch(
        ReadOnlySpan<byte> core,
        ReadOnlySpan<byte> bootSectorTemplate,
        IReadOnlyList<long> fileSectors,
        bool singleSectorReads,
        ReadOnlySpan<byte> directory)
    {
        var image = new byte[PaddedLength(core.Length)];
        core.CopyTo(image);
        var boot = bootSectorTemplate.ToArray();

        var sectors = FileSectors(core.Length);
        if (fileSectors.Count < sectors)
        {
            throw new InvalidDataException("ldlinux.sys on the volume is shorter than the loader.");
        }

        var area = FindPatchArea(image);
        var epa = area.EpaOffset;

        // The first sector is loaded by the boot sector, which therefore carries its address.
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(Field(image, epa + 14)), (uint)fileSectors[0]);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(Field(image, epa + 16)), (uint)((ulong)fileSectors[0] >> 32));

        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(area.Offset + 8), (ushort)(sectors - AdvSectors));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(area.Offset + 10), AdvSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(area.Offset + 12), (uint)(image.Length >> 2));
        if (singleSectorReads)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(area.Offset + 20), 1);
        }

        var extentsAt = Field(image, epa + 10);
        var extentCapacity = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(epa + 12));
        var dataSectors = fileSectors.Skip(1).Take(sectors - 1 - AdvSectors).ToArray();
        WriteExtents(image.AsSpan(extentsAt, extentCapacity * ExtentBytes), dataSectors);

        var advAt = Field(image, epa);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(advAt), (ulong)fileSectors[sectors - AdvSectors]);
        BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(advAt + 8), (ulong)fileSectors[sectors - 1]);

        if (!directory.IsEmpty)
        {
            var dirAt = Field(image, epa + 2);
            var dirCapacity = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(epa + 4));
            if (directory.Length + 1 > dirCapacity)
            {
                throw new InvalidDataException("The Syslinux directory path is too long.");
            }

            directory.CopyTo(image.AsSpan(dirAt));
            image[dirAt + directory.Length] = 0;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(area.Offset + 16), 0);
        var checksum = LdlinuxMagic;
        for (var offset = 0; offset < image.Length; offset += 4)
        {
            checksum -= BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(area.Offset + 16), checksum);
        return new PatchedSyslinux(image, boot);
    }

    /// <summary>
    /// Puts the Syslinux code into an existing FAT boot sector and keeps the volume's own BIOS parameter block
    /// and boot signature; the jump and OEM name ("SYSLINUX") at the start and the code from byte 90 on are replaced.
    /// </summary>
    public static byte[] MakeBootSector(ReadOnlySpan<byte> patchedTemplate, ReadOnlySpan<byte> existing)
    {
        var sector = existing[..SectorSize].ToArray();
        patchedTemplate[..FatHeadLength].CopyTo(sector);
        patchedTemplate[FatCodeStart..FatCodeEnd].CopyTo(sector.AsSpan(FatCodeStart));
        return sector;
    }

    /// <summary>
    /// Reads a patched ldlinux.sys back: the checksum over the whole image has to come out as the magic number and the
    /// extents have to list exactly the given sectors. Returns null when everything agrees, otherwise what is wrong.
    /// </summary>
    public static string? Validate(ReadOnlySpan<byte> image, IReadOnlyList<long> fileSectors, ReadOnlySpan<byte> bootSector)
    {
        if (image.Length % SectorSize != 0 || fileSectors.Count < (image.Length / SectorSize) + AdvSectors)
        {
            return "length of ldlinux.sys does not match its sector map";
        }

        var sum = 0u;
        for (var offset = 0; offset < image.Length; offset += 4)
        {
            sum += BinaryPrimitives.ReadUInt32LittleEndian(image[offset..]);
        }

        if (sum != LdlinuxMagic)
        {
            return "ldlinux.sys checksum is wrong";
        }

        var area = FindPatchArea(image);
        var epa = area.EpaOffset;
        var firstAt = Field(image, epa + 14);
        var first = BinaryPrimitives.ReadUInt32LittleEndian(bootSector[firstAt..]) | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bootSector[Field(image, epa + 16)..]) << 32);
        if (first != (ulong)fileSectors[0])
        {
            return "boot sector does not point at the first sector of ldlinux.sys";
        }

        var extentsAt = Field(image, epa + 10);
        var capacity = BinaryPrimitives.ReadUInt16LittleEndian(image[(epa + 12)..]);
        var expected = fileSectors.Skip(1).Take((image.Length / SectorSize) - 1).Select(sector => sector).ToList();
        var listed = new List<long>();
        for (var i = 0; i < capacity; i++)
        {
            var at = extentsAt + (i * ExtentBytes);
            var lba = BinaryPrimitives.ReadUInt64LittleEndian(image[at..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(image[(at + 8)..]);
            if (length == 0)
            {
                break;
            }

            for (var s = 0; s < length; s++)
            {
                listed.Add((long)lba + s);
            }
        }

        return listed.SequenceEqual(expected) ? null : "the extents in ldlinux.sys do not match the file on the volume";
    }

    private static void WriteExtents(Span<byte> table, long[] sectors)
    {
        table.Clear();
        var extents = GenerateExtents(sectors);
        if (extents.Count * ExtentBytes > table.Length)
        {
            throw new InvalidDataException("ldlinux.sys is too fragmented on the volume for the loader's extent table.");
        }

        for (var i = 0; i < extents.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(table[(i * ExtentBytes)..], extents[i].Lba);
            BinaryPrimitives.WriteUInt16LittleEndian(table[((i * ExtentBytes) + 8)..], extents[i].Length);
        }
    }

    /// <summary>Joins consecutive sectors into runs, never longer than 64 KiB and never across a 64 KiB boundary of the load address.</summary>
    private static List<(ulong Lba, ushort Length)> GenerateExtents(long[] sectors)
    {
        var extents = new List<(ulong, ushort)>();
        var address = LoadAddress;
        var baseAddress = address;
        ulong lba = 0;
        uint length = 0;

        foreach (var sector in sectors)
        {
            if (length != 0)
            {
                var bytes = (length + 1) * SectorSize;
                if ((ulong)sector == lba + length && bytes < 65536 && ((address ^ (baseAddress + bytes - 1)) & 0xFFFF0000) == 0)
                {
                    length++;
                    address += SectorSize;
                    continue;
                }

                extents.Add((lba, (ushort)length));
            }

            baseAddress = address;
            lba = (ulong)sector;
            length = 1;
            address += SectorSize;
        }

        if (length != 0)
        {
            extents.Add((lba, (ushort)length));
        }

        return extents;
    }

    private static (int Offset, int EpaOffset) FindPatchArea(ReadOnlySpan<byte> image)
    {
        for (var offset = 0; offset + 24 <= image.Length; offset += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(image[offset..]) == LdlinuxMagic)
            {
                return (offset, BinaryPrimitives.ReadUInt16LittleEndian(image[(offset + 22)..]));
            }
        }

        throw new InvalidDataException("The Syslinux core has no patch area.");
    }

    private static int Field(ReadOnlySpan<byte> image, int at) => BinaryPrimitives.ReadUInt16LittleEndian(image[at..]);
}

/// <summary>The core with its sector map written in, and the boot sector template that points at it.</summary>
internal sealed record PatchedSyslinux(byte[] Core, byte[] BootSectorTemplate);
