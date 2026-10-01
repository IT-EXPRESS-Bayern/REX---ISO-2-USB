// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical;

public static class SectorMath
{
    /// <summary>User data per sector of a data disc; every CD, DVD and BD image Bootrix handles uses it.</summary>
    public const int SectorSize = 2048;

    /// <summary>Largest amount ISO 9660 can address in one file (32-bit length field).</summary>
    public const long Iso9660MaxFileSize = uint.MaxValue;

    public static long ToBytes(long sectors) => checked(sectors * SectorSize);

    public static long SectorsFor(long bytes) => (bytes + SectorSize - 1) / SectorSize;

    public static long RoundUpToSector(long bytes) => SectorsFor(bytes) * SectorSize;

    /// <summary>
    /// Sectors per second at 1x. The values are the ones IMAPI uses for its speed descriptors,
    /// not the nominal data rates: DVD 1x is 1385 kB/s, which IMAPI rounds to 680 sectors.
    /// </summary>
    public static int SectorsPerSecondAt1x(OpticalMediaFamily family) => family switch
    {
        OpticalMediaFamily.Cd => 75,
        OpticalMediaFamily.Dvd => 680,
        OpticalMediaFamily.BluRay => 2195,
        _ => 0,
    };

    public static int SpeedFactor(OpticalMediaFamily family, int sectorsPerSecond)
    {
        var baseRate = SectorsPerSecondAt1x(family);
        return baseRate == 0 || sectorsPerSecond <= 0
            ? 0
            : (int)Math.Round(sectorsPerSecond / (double)baseRate, MidpointRounding.AwayFromZero);
    }

    /// <summary>Speed in IMAPI units; -1 asks for the fastest speed the drive offers for the inserted disc.</summary>
    public static int ToSectorsPerSecond(OpticalMediaFamily family, int? factor) =>
        factor is not { } x || x <= 0 ? -1 : checked(x * SectorsPerSecondAt1x(family));

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB"),
        >= 1L << 20 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.#} MB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
    };
}
