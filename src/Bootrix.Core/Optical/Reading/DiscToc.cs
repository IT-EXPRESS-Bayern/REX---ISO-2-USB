// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Reading;

/// <summary>Disc type byte of a session (the PSEC field of the A0 point in the full TOC).</summary>
public enum SessionDiscType : byte
{
    CdDaOrCdRom = 0x00,
    CdInteractive = 0x10,
    CdRomXa = 0x20,
}

public sealed record TocTrack(int Number, int Session, bool IsData, int StartLba, int LengthSectors, bool CopyPermitted)
{
    public int EndLba => StartLba + LengthSectors;
}

public sealed record TocSession(int Number, SessionDiscType DiscType, int LeadOutLba, IReadOnlyList<TocTrack> Tracks);

public enum DiscContent
{
    /// <summary>Data tracks only (one or more sessions).</summary>
    Data,

    /// <summary>Audio tracks only.</summary>
    Audio,

    /// <summary>Audio and data on the same disc, in one session (mixed mode) or in different ones (CD-Extra).</summary>
    Mixed,
}

public sealed record DiscToc(IReadOnlyList<TocSession> Sessions)
{
    public IEnumerable<TocTrack> Tracks => Sessions.SelectMany(s => s.Tracks);

    public bool HasData => Tracks.Any(t => t.IsData);

    public bool HasAudio => Tracks.Any(t => !t.IsData);

    public bool IsMultiSession => Sessions.Count > 1;

    public DiscContent Content => (HasData, HasAudio) switch
    {
        (true, true) => DiscContent.Mixed,
        (false, true) => DiscContent.Audio,
        _ => DiscContent.Data,
    };

    /// <summary>End of the last session; the first sector past everything the TOC describes.</summary>
    public int LeadOutLba => Sessions.Count == 0 ? 0 : Sessions[^1].LeadOutLba;
}
