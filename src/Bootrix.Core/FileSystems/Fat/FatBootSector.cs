// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.IO;

namespace Bootrix.Core.FileSystems.Fat;

/// <summary>
/// Builds the boot sector and, on FAT32, the rest of the reserved area (FSInfo, the sector 2
/// continuation and the backup copy in sectors 6 to 8).
/// </summary>
internal static class FatBootSector
{
    public const int Fat32FsInfoSector = 1;
    public const int Fat32BackupSector = 6;
    public const int Fat32RootCluster = 2;

    private const int Fat1216CodeOffset = 0x3E;
    private const int Fat32CodeOffset = 0x5A;
    private const int Fat1216Jump = 0x3C;
    private const int Fat32Jump = 0x58;
    private const int FsInfoNextFreeOffset = 0x1EC;
    private const int FsInfoFreeCountOffset = 0x1E8;
    private const int BackupSectors = 3;

    /// <summary>The first sectors of the volume, ready to be written at offset zero.</summary>
    public static byte[] BuildReservedArea(FatLayout layout, FatFormatOptions options, uint volumeId, string label)
    {
        var isFat32 = layout.Type == FatType.Fat32;
        var bps = layout.BytesPerSector;
        var bootCode = ParseBootCode(options.BootCode, layout);

        var sectorCount = isFat32 ? Math.Max(Fat32BackupSector + BackupSectors, bootCode?.Sectors ?? 0) : 1;
        var area = new byte[sectorCount * bps];

        for (var i = 1; i < (bootCode?.Sectors ?? 0); i++)
        {
            bootCode!.Bytes.AsSpan(i * bps, bps).CopyTo(area.AsSpan(i * bps));
        }

        WriteSector0(area.AsSpan(0, bps), layout, options, volumeId, label, bootCode?.Bytes);
        if (!isFat32)
        {
            return area;
        }

        WriteFsInfo(area.AsSpan(Fat32FsInfoSector * bps, bps), layout);
        MarkBootSignature(area.AsSpan(2 * bps, bps));

        // Windows keeps a full copy of the three boot sectors; FSInfo in the copy is the same.
        area.AsSpan(0, BackupSectors * bps).CopyTo(area.AsSpan(Fat32BackupSector * bps));
        return area;
    }

    private static void WriteSector0(
        Span<byte> sector, FatLayout layout, FatFormatOptions options, uint volumeId, string label, byte[]? bootCode)
    {
        var isFat32 = layout.Type == FatType.Fat32;
        var codeOffset = isFat32 ? Fat32CodeOffset : Fat1216CodeOffset;

        if (bootCode is null)
        {
            sector[0] = 0xEB;
            sector[1] = (byte)(isFat32 ? Fat32Jump : Fat1216Jump);
            sector[2] = 0x90;
            BootMessageStub.ForFileSystem().CopyTo(sector[codeOffset..]);
        }
        else
        {
            bootCode.AsSpan(0, 3).CopyTo(sector);
            bootCode.AsSpan(codeOffset, sector.Length - 2 - codeOffset).CopyTo(sector[codeOffset..]);
        }

        Encoding.ASCII.GetBytes(options.OemName.PadRight(8).AsSpan(0, 8), sector[3..11]);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[11..], (ushort)layout.BytesPerSector);
        sector[13] = (byte)layout.SectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(sector[14..], (ushort)layout.ReservedSectors);
        sector[16] = (byte)layout.FatCount;
        BinaryPrimitives.WriteUInt16LittleEndian(sector[17..], (ushort)layout.RootEntries);

        // The 16-bit total is used whenever it fits, except on FAT32 where it must stay zero.
        var smallTotal = !isFat32 && layout.TotalSectors < 0x10000;
        BinaryPrimitives.WriteUInt16LittleEndian(sector[19..], smallTotal ? (ushort)layout.TotalSectors : (ushort)0);
        sector[21] = options.MediaDescriptor;
        BinaryPrimitives.WriteUInt16LittleEndian(sector[22..], isFat32 ? (ushort)0 : (ushort)layout.SectorsPerFat);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[24..], (ushort)options.SectorsPerTrack);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[26..], (ushort)options.Heads);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[28..], options.HiddenSectors);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[32..], smallTotal ? 0 : (uint)layout.TotalSectors);

        var labelField = FatLabel.ToField(label.Length == 0 ? FatLabel.Unlabeled : label);
        if (isFat32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sector[36..], (uint)layout.SectorsPerFat);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[44..], Fat32RootCluster);
            BinaryPrimitives.WriteUInt16LittleEndian(sector[48..], Fat32FsInfoSector);
            BinaryPrimitives.WriteUInt16LittleEndian(sector[50..], Fat32BackupSector);
            WriteExtendedFields(sector[64..], options.DriveNumber, volumeId, labelField, "FAT32   ");
        }
        else
        {
            WriteExtendedFields(sector[36..], options.DriveNumber, volumeId, labelField, layout.Type == FatType.Fat12 ? "FAT12   " : "FAT16   ");
        }

        MarkBootSignature(sector);
    }

    /// <summary>Drive number, extended boot signature, serial, label and file system name: the same 26 bytes on every type.</summary>
    private static void WriteExtendedFields(Span<byte> field, byte driveNumber, uint volumeId, byte[] label, string typeName)
    {
        field[0] = driveNumber;
        field[2] = 0x29;
        BinaryPrimitives.WriteUInt32LittleEndian(field[3..], volumeId);
        label.CopyTo(field[7..]);
        Encoding.ASCII.GetBytes(typeName, field[18..]);
    }

    private static void WriteFsInfo(Span<byte> sector, FatLayout layout)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(sector, 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[0x1E4..], 0x61417272);

        // Cluster 2 holds the root directory; everything else is free.
        BinaryPrimitives.WriteUInt32LittleEndian(sector[FsInfoFreeCountOffset..], (uint)(layout.ClusterCount - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(sector[FsInfoNextFreeOffset..], Fat32RootCluster + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[0x1FC..], 0xAA550000);
    }

    /// <summary>The 0x55 0xAA marker sits at the end of the first 512 bytes and, on larger sectors, also at the end of the sector.</summary>
    private static void MarkBootSignature(Span<byte> sector)
    {
        sector[510] = 0x55;
        sector[511] = 0xAA;
        if (sector.Length > 512)
        {
            sector[^2] = 0x55;
            sector[^1] = 0xAA;
        }
    }

    private static BootCodeImage? ParseBootCode(byte[]? code, FatLayout layout)
    {
        if (code is null)
        {
            return null;
        }

        var bps = layout.BytesPerSector;
        if (code.Length == 512)
        {
            return new BootCodeImage(code.Length == bps ? code : Pad(code, bps), 1);
        }

        if (code.Length == 0 || code.Length % bps != 0)
        {
            throw Invalid($"boot code of {code.Length} bytes is neither 512 bytes nor a whole number of {bps}-byte sectors");
        }

        var sectors = code.Length / bps;
        if (layout.Type != FatType.Fat32 && sectors > 1)
        {
            throw Invalid("only FAT32 has room for boot code beyond the first sector");
        }

        return sectors <= layout.ReservedSectors
            ? new BootCodeImage(code, sectors)
            : throw Invalid($"{sectors} boot sectors do not fit {layout.ReservedSectors} reserved sectors");
    }

    private static byte[] Pad(byte[] code, int length)
    {
        var padded = new byte[length];
        code.CopyTo(padded, 0);
        return padded;
    }

    private sealed record BootCodeImage(byte[] Bytes, int Sectors);

    private static BootrixException Invalid(string detail) =>
        new(ErrorCode.InvalidSpec, detail) { Arguments = [detail] };
}
