// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Catalog.Distros.Common;

/// <summary>
/// A dotted number such as "24.04.5.1" or "13.7.0". Compared segment by segment; a missing segment counts as zero,
/// so "24.04" equals "24.04.0" and is older than "24.04.0.1".
/// </summary>
internal sealed class NumericVersion : IComparable<NumericVersion>, IEquatable<NumericVersion>
{
    private readonly int[] _parts;

    private NumericVersion(int[] parts)
    {
        _parts = parts;
    }

    public int Major => _parts[0];

    public int Minor => _parts.Length > 1 ? _parts[1] : 0;

    /// <summary>The text with leading zeros kept ("24.04"), which is how distributions spell directories.</summary>
    public string Text { get; private init; } = string.Empty;

    public static bool TryParse(string? text, out NumericVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var segments = text.Trim().Split('.');
        var parts = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i]))
            {
                return false;
            }
        }

        version = new NumericVersion(parts) { Text = text.Trim() };
        return true;
    }

    public static NumericVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"'{text}' is not a dotted version number.");

    public int CompareTo(NumericVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var length = Math.Max(_parts.Length, other._parts.Length);
        for (var i = 0; i < length; i++)
        {
            var difference = Part(i).CompareTo(other.Part(i));
            if (difference != 0)
            {
                return difference;
            }
        }

        return 0;
    }

    public bool Equals(NumericVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => Equals(obj as NumericVersion);

    public override int GetHashCode()
    {
        // Trailing zeros do not matter for equality, so they must not matter for the hash.
        var hash = new HashCode();
        var last = _parts.Length;
        while (last > 1 && _parts[last - 1] == 0)
        {
            last--;
        }

        for (var i = 0; i < last; i++)
        {
            hash.Add(_parts[i]);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Text;

    public static bool operator >(NumericVersion left, NumericVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(NumericVersion left, NumericVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(NumericVersion left, NumericVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(NumericVersion left, NumericVersion right) => left.CompareTo(right) <= 0;

    private int Part(int index) => index < _parts.Length ? _parts[index] : 0;
}
