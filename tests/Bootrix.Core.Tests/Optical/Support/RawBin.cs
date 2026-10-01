// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical;

namespace Bootrix.Core.Tests.Optical.Support;

internal static class RawBin
{
    /// <summary>
    /// Lays an ISO out as MODE1/2352 sectors the way ECMA-130 describes them: 12 bytes sync, 4 bytes header with
    /// the absolute MSF address and mode 1, 2048 bytes of data, then error detection fields. Those are filled with
    /// a recognisable pattern; nothing in Bootrix looks at them. Optional pregap sectors with zero data come first.
    /// </summary>
    public static byte[] FromIso(byte[] iso, int pregapSectors = 0)
    {
        var sectors = iso.Length / 2048;
        var bin = new byte[(pregapSectors + sectors) * 2352];
        for (var i = 0; i < pregapSectors + sectors; i++)
        {
            var sector = bin.AsSpan(i * 2352, 2352);
            sector[0] = 0x00;
            sector[1..11].Fill(0xFF);
            sector[11] = 0x00;
            var msf = Msf.FromLba(i - pregapSectors);
            sector[12] = ToBcd(msf.Minutes);
            sector[13] = ToBcd(msf.Seconds);
            sector[14] = ToBcd(msf.Frames);
            sector[15] = 1;
            if (i >= pregapSectors)
            {
                iso.AsSpan((i - pregapSectors) * 2048, 2048).CopyTo(sector[16..]);
            }

            sector[2064..].Fill(0xAA);
        }

        return bin;
    }

    private static byte ToBcd(int value) => (byte)(value / 10 << 4 | value % 10);
}
