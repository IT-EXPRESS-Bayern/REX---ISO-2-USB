// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Images;

public enum CueTrackMode
{
    Audio,
    Mode1Data2048,
    Mode1Raw2352,
    Mode2Data2336,
    Mode2Raw2352,
    CdGraphics2448,
    CdiData2336,
    CdiRaw2352,
}

public static class CueTrackModes
{
    /// <summary>Bytes one sector takes in the .bin file.</summary>
    public static int SectorSize(this CueTrackMode mode) => mode switch
    {
        CueTrackMode.Mode1Data2048 => 2048,
        CueTrackMode.Mode2Data2336 or CueTrackMode.CdiData2336 => 2336,
        CueTrackMode.CdGraphics2448 => 2448,
        _ => 2352,
    };

    public static bool IsData(this CueTrackMode mode) => mode != CueTrackMode.Audio && mode != CueTrackMode.CdGraphics2448;

    public static bool TryParse(string text, out CueTrackMode mode)
    {
        CueTrackMode? parsed = text.ToUpperInvariant() switch
        {
            "AUDIO" => CueTrackMode.Audio,
            "MODE1/2048" => CueTrackMode.Mode1Data2048,
            "MODE1/2352" => CueTrackMode.Mode1Raw2352,
            "MODE2/2336" => CueTrackMode.Mode2Data2336,
            "MODE2/2352" => CueTrackMode.Mode2Raw2352,
            "CDG" => CueTrackMode.CdGraphics2448,
            "CDI/2336" => CueTrackMode.CdiData2336,
            "CDI/2352" => CueTrackMode.CdiRaw2352,
            _ => null,
        };

        mode = parsed.GetValueOrDefault();
        return parsed.HasValue;
    }
}

/// <param name="Name">Path as written in the sheet, relative to the folder of the .cue file.</param>
/// <param name="Type">BINARY, WAVE, MP3, AIFF or MOTOROLA (big-endian binary audio).</param>
public sealed record CueFile(string Name, string Type)
{
    public bool IsBinary => string.Equals(Type, "BINARY", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Position">Offset inside the file of the track, in frames.</param>
public sealed record CueIndex(int Number, Msf Position);

public sealed record CueTrack(
    int Number,
    CueTrackMode Mode,
    CueFile File,
    IReadOnlyList<CueIndex> Indexes,
    Msf? Pregap,
    Msf? Postgap,
    IReadOnlyList<string> Flags,
    string? Title,
    string? Performer)
{
    /// <summary>INDEX 01 is where the track proper starts; INDEX 00, when present, is the pregap that precedes it.</summary>
    public CueIndex? Start => Indexes.FirstOrDefault(i => i.Number == 1);

    public CueIndex? PregapIndex => Indexes.FirstOrDefault(i => i.Number == 0);
}

public sealed record CueSheet(
    IReadOnlyList<CueFile> Files,
    IReadOnlyList<CueTrack> Tracks,
    string? Catalog,
    string? Title,
    string? Performer);
