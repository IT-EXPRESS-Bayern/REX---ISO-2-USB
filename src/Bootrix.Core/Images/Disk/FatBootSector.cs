// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Numerics;

namespace Bootrix.Core.Images.Disk;

/// <summary>
/// Plausibility checks for the BIOS parameter block of FAT volumes. A volume image also ends in 0x55AA,
/// so without these checks a floppy image would be taken for a disk with a partition table.
/// </summary>
internal static class FatBootSector
{
    public static bool LooksLikeFatBootSector(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512 || sector[510] != 0x55 || sector[511] != 0xAA)
        {
            return false;
        }

        // x86 short jump with NOP, or a near jump.
        if (!(sector[0] == 0xEB && sector[2] == 0x90) && sector[0] != 0xE9)
        {
            return false;
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        var sectorsPerCluster = sector[13];
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector[14..]);
        var fats = sector[16];
        var media = sector[21];

        return bytesPerSector is 512 or 1024 or 2048 or 4096
            && sectorsPerCluster != 0 && BitOperations.IsPow2(sectorsPerCluster)
            && reserved >= 1
            && fats is 1 or 2
            && (media >= 0xF8 || media == 0xF0);
    }

    /// <summary>Size of the volume in bytes according to the BPB; 0 when the sector is not a FAT boot sector.</summary>
    public static long VolumeBytes(ReadOnlySpan<byte> sector)
    {
        if (!LooksLikeFatBootSector(sector))
        {
            return 0;
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(sector[11..]);
        long total = BinaryPrimitives.ReadUInt16LittleEndian(sector[19..]);
        if (total == 0)
        {
            total = BinaryPrimitives.ReadUInt32LittleEndian(sector[32..]);
        }

        return total * bytesPerSector;
    }
}
