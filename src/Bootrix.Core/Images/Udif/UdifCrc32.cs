// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// CRC-32 as UDIF stores it: the usual reflected polynomial 0xEDB88320 (the zlib and PKZIP CRC).
/// Long runs of zeros, which sparse images consist of, are folded in without touching memory.
/// </summary>
internal struct UdifCrc32
{
    private const uint Polynomial = 0xEDB88320;

    private static readonly uint[] Table = BuildTable();
    private static readonly uint[] SquaredPowers = BuildSquaredPowers();

    private uint _register = uint.MaxValue;

    public UdifCrc32()
    {
    }

    public readonly uint Value => ~_register;

    public void Append(ReadOnlySpan<byte> data)
    {
        var register = _register;
        while (data.Length >= 8)
        {
            var low = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ register;
            var high = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            register = Table[(7 * 256) + (int)(low & 0xFF)]
                ^ Table[(6 * 256) + (int)((low >> 8) & 0xFF)]
                ^ Table[(5 * 256) + (int)((low >> 16) & 0xFF)]
                ^ Table[(4 * 256) + (int)(low >> 24)]
                ^ Table[(3 * 256) + (int)(high & 0xFF)]
                ^ Table[(2 * 256) + (int)((high >> 8) & 0xFF)]
                ^ Table[256 + (int)((high >> 16) & 0xFF)]
                ^ Table[(int)(high >> 24)];
            data = data[8..];
        }

        foreach (var b in data)
        {
            register = Table[(byte)(register ^ b)] ^ (register >> 8);
        }

        _register = register;
    }

    /// <summary>Appends <paramref name="count"/> zero bytes by multiplying the register with x^(8 * count).</summary>
    public void AppendZeros(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        _register = MultiplyModulo(PowerOfX(count, 3), _register);
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new UdifCrc32();
        crc.Append(data);
        return crc.Value;
    }

    // Eight consecutive tables for slicing-by-8: table k advances a byte through k further zero bytes.
    private static uint[] BuildTable()
    {
        var table = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ Polynomial : value >> 1;
            }

            table[i] = value;
        }

        for (var i = 256; i < table.Length; i++)
        {
            var previous = table[i - 256];
            table[i] = (previous >> 8) ^ table[previous & 0xFF];
        }

        return table;
    }

    // x^(2^n) modulo the polynomial, for n in 0..31, in the bit-reflected representation used by CRC-32.
    private static uint[] BuildSquaredPowers()
    {
        var powers = new uint[32];
        var p = 1u << 30;
        powers[0] = p;
        for (var i = 1; i < powers.Length; i++)
        {
            p = MultiplyModulo(p, p);
            powers[i] = p;
        }

        return powers;
    }

    // x^(n * 2^k) modulo the polynomial.
    private static uint PowerOfX(long n, int k)
    {
        var result = 1u << 31;
        while (n != 0)
        {
            if ((n & 1) != 0)
            {
                result = MultiplyModulo(SquaredPowers[k & 31], result);
            }

            n >>= 1;
            k++;
        }

        return result;
    }

    private static uint MultiplyModulo(uint a, uint b)
    {
        var mask = 1u << 31;
        var product = 0u;
        while (true)
        {
            if ((a & mask) != 0)
            {
                product ^= b;
                if ((a & (mask - 1)) == 0)
                {
                    break;
                }
            }

            mask >>= 1;
            b = (b & 1) != 0 ? (b >> 1) ^ Polynomial : b >> 1;
        }

        return product;
    }
}
