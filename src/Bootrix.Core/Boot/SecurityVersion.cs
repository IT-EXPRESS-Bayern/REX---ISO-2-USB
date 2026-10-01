// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Boot;

/// <summary>
/// Microsoft's Secure Version Number. It is stored as two little-endian 16-bit values, minor first,
/// both in the BOOTMGRSECURITYVERSIONNUMBER resource of a boot manager and in the DBX SVN entries.
/// </summary>
public readonly record struct SecurityVersion(ushort Major, ushort Minor) : IComparable<SecurityVersion>
{
    public int CompareTo(SecurityVersion other)
    {
        var byMajor = Major.CompareTo(other.Major);
        return byMajor != 0 ? byMajor : Minor.CompareTo(other.Minor);
    }

    public static bool operator <(SecurityVersion left, SecurityVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SecurityVersion left, SecurityVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SecurityVersion left, SecurityVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SecurityVersion left, SecurityVersion right) => left.CompareTo(right) >= 0;

    public static bool TryParse(string? text, out SecurityVersion version)
    {
        version = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 2 }
            || !ushort.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }

        version = new SecurityVersion(major, minor);
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}");
}
