// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>Plain bytewise CRC-32, deliberately unrelated to the production implementation.</summary>
internal sealed class TestCrc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        var value = (uint)i;
        for (var bit = 0; bit < 8; bit++)
        {
            value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
        }

        return value;
    }).ToArray();

    private uint _register = 0xFFFFFFFF;

    public uint Value => ~_register;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new TestCrc32();
        crc.Append(data);
        return crc.Value;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _register = Table[(byte)(_register ^ b)] ^ (_register >> 8);
        }
    }

    public void AppendZeros(long count)
    {
        var zeros = new byte[64 * 1024];
        while (count > 0)
        {
            var n = (int)Math.Min(count, zeros.Length);
            Append(zeros.AsSpan(0, n));
            count -= n;
        }
    }
}
