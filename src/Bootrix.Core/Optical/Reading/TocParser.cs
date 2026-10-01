// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Optical.Reading;

/// <summary>
/// Parses the answers to the MMC READ TOC command (formats 0 and 2) as Windows returns them from
/// IOCTL_CDROM_READ_TOC_EX. The full TOC is preferred because it is the only form that says which
/// session a track belongs to.
/// </summary>
public static class TocParser
{
    private const int HeaderSize = 4;
    private const int FullDescriptorSize = 11;
    private const int BasicDescriptorSize = 8;
    private const byte PointFirstTrack = 0xA0;
    private const byte PointLastTrack = 0xA1;
    private const byte PointLeadOut = 0xA2;
    private const byte BasicLeadOutTrack = 0xAA;
    private const int DataTrackControlBit = 0x04;
    private const int CopyPermittedControlBit = 0x02;

    /// <summary>Parses a full TOC (READ TOC format 2). Returns null when the data holds no usable track.</summary>
    public static DiscToc? ParseFull(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            return null;
        }

        // The length field counts the two bytes after itself; drives sometimes report more than they send.
        var declared = BinaryPrimitives.ReadUInt16BigEndian(data) - 2;
        var available = Math.Min(Math.Max(declared, 0), data.Length - HeaderSize) / FullDescriptorSize;

        var perSession = new SortedDictionary<int, SessionBuilder>();
        for (var i = 0; i < available; i++)
        {
            var d = data.Slice(HeaderSize + i * FullDescriptorSize, FullDescriptorSize);
            var adr = d[1] >> 4;
            if (adr != 1)
            {
                continue;
            }

            var session = d[0];
            if (!perSession.TryGetValue(session, out var builder))
            {
                builder = new SessionBuilder(session);
                perSession[session] = builder;
            }

            var control = d[1] & 0x0F;
            var point = d[3];
            var position = new Msf(d[8], d[9], d[10]);
            switch (point)
            {
                case PointFirstTrack:
                    builder.DiscType = Enum.IsDefined((SessionDiscType)d[9]) ? (SessionDiscType)d[9] : SessionDiscType.CdDaOrCdRom;
                    break;
                case PointLastTrack:
                    break;
                case PointLeadOut:
                    builder.LeadOutLba = position.ToLba();
                    break;
                case >= 1 and <= 99:
                    builder.AddTrack(point, position.ToLba(), control);
                    break;
            }
        }

        var sessions = perSession.Values.Where(b => b.Starts.Count > 0 && b.LeadOutLba is not null).Select(b => b.Build()).ToList();
        return sessions.Count == 0 ? null : new DiscToc(sessions);
    }

    /// <summary>Parses the plain TOC (READ TOC format 0), which cannot tell sessions apart; all tracks end up in session 1.</summary>
    public static DiscToc? ParseBasic(ReadOnlySpan<byte> data, bool addressesAreMsf)
    {
        if (data.Length < HeaderSize)
        {
            return null;
        }

        var declared = BinaryPrimitives.ReadUInt16BigEndian(data) - 2;
        var available = Math.Min(Math.Max(declared, 0), data.Length - HeaderSize) / BasicDescriptorSize;

        var builder = new SessionBuilder(1);
        for (var i = 0; i < available; i++)
        {
            var d = data.Slice(HeaderSize + i * BasicDescriptorSize, BasicDescriptorSize);
            var control = d[1] & 0x0F;
            var number = d[2];
            var lba = addressesAreMsf
                ? new Msf(d[5], d[6], d[7]).ToLba()
                : BinaryPrimitives.ReadInt32BigEndian(d[4..]);

            if (number == BasicLeadOutTrack)
            {
                builder.LeadOutLba = lba;
            }
            else if (number is >= 1 and <= 99)
            {
                builder.AddTrack(number, lba, control);
            }
        }

        return builder.Starts.Count == 0 || builder.LeadOutLba is null ? null : new DiscToc([builder.Build()]);
    }

    private sealed class SessionBuilder(int number)
    {
        public List<(int Number, int StartLba, bool IsData, bool CopyPermitted)> Starts { get; } = [];

        public SessionDiscType DiscType { get; set; }

        public int? LeadOutLba { get; set; }

        // Some drives repeat the descriptors; the first one wins.
        public void AddTrack(int trackNumber, int startLba, int control)
        {
            if (Starts.TrueForAll(s => s.Number != trackNumber))
            {
                Starts.Add((trackNumber, startLba, (control & DataTrackControlBit) != 0, (control & CopyPermittedControlBit) != 0));
            }
        }

        public TocSession Build()
        {
            var ordered = Starts.OrderBy(s => s.Number).ToList();
            var leadOut = LeadOutLba!.Value;
            var tracks = new List<TocTrack>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var end = i + 1 < ordered.Count ? ordered[i + 1].StartLba : leadOut;
                var t = ordered[i];
                tracks.Add(new TocTrack(t.Number, number, t.IsData, t.StartLba, Math.Max(0, end - t.StartLba), t.CopyPermitted));
            }

            return new TocSession(number, DiscType, leadOut, tracks);
        }
    }
}
