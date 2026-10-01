// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Optical;

/// <summary>
/// Minutes:seconds:frames as used by CUE sheets and the CD table of contents. A frame is one
/// sector, 75 of them make a second. The TOC counts from the start of the lead-in area, which is
/// 150 sectors (two seconds) before LBA 0; CUE sheets count from the start of their file.
/// </summary>
public readonly record struct Msf(int Minutes, int Seconds, int Frames) : IComparable<Msf>
{
    public const int FramesPerSecond = 75;

    /// <summary>Distance between the MSF origin and LBA 0.</summary>
    public const int LeadInFrames = 2 * FramesPerSecond;

    public static Msf FromFrames(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        return new Msf(frames / (60 * FramesPerSecond), frames / FramesPerSecond % 60, frames % FramesPerSecond);
    }

    /// <summary>Absolute disc address of an LBA, the form READ TOC reports.</summary>
    public static Msf FromLba(int lba) => FromFrames(lba + LeadInFrames);

    public int ToFrames() => (Minutes * 60 + Seconds) * FramesPerSecond + Frames;

    public int ToLba() => ToFrames() - LeadInFrames;

    public static bool TryParse(ReadOnlySpan<char> text, out Msf value)
    {
        value = default;
        var parts = text.Trim();
        var first = parts.IndexOf(':');
        if (first <= 0)
        {
            return false;
        }

        var rest = parts[(first + 1)..];
        var second = rest.IndexOf(':');
        if (second <= 0)
        {
            return false;
        }

        if (!TryNumber(parts[..first], out var minutes)
            || !TryNumber(rest[..second], out var seconds)
            || !TryNumber(rest[(second + 1)..], out var frames)
            || seconds >= 60
            || frames >= FramesPerSecond)
        {
            return false;
        }

        value = new Msf(minutes, seconds, frames);
        return true;
    }

    public static Msf Parse(string text) =>
        TryParse(text, out var value) ? value : throw new FormatException($"'{text}' is not a valid mm:ss:ff time.");

    public int CompareTo(Msf other) => ToFrames().CompareTo(other.ToFrames());

    public static bool operator <(Msf left, Msf right) => left.CompareTo(right) < 0;

    public static bool operator >(Msf left, Msf right) => left.CompareTo(right) > 0;

    public static bool operator <=(Msf left, Msf right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Msf left, Msf right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Minutes:D2}:{Seconds:D2}:{Frames:D2}");

    private static bool TryNumber(ReadOnlySpan<char> text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
