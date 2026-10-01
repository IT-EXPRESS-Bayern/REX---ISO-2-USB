// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>CRC-32 as used by the GPT header and entry array (IEEE 802.3, reflected, polynomial 0xEDB88320).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320u : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
