// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>
/// The reflected CRC-16 with polynomial 0x8005 that ext4 uses for group descriptor checksums
/// (the kernel's lib/crc16 with an initial value of 0xFFFF).
/// </summary>
internal static class Crc16
{
    private static readonly ushort[] Table = BuildTable();

    public static ushort Update(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = (ushort)((crc >> 8) ^ Table[(crc ^ b) & 0xFF]);
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (ushort)i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (ushort)((value >> 1) ^ 0xA001) : (ushort)(value >> 1);
            }

            table[i] = value;
        }

        return table;
    }
}
