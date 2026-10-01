// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical.Reading;
using Bootrix.Core.Tests.Optical.Support;

namespace Bootrix.Core.Tests.Optical;

public class TocParserTests
{
    [Fact]
    public void SingleSessionDataDisc()
    {
        var toc = TocParser.ParseFull(OpticalTestData.FullToc((1, [(1, true, 0)], 330_000, 0x00)));

        Assert.NotNull(toc);
        var session = Assert.Single(toc.Sessions);
        var track = Assert.Single(session.Tracks);
        Assert.True(track.IsData);
        Assert.Equal(0, track.StartLba);
        Assert.Equal(330_000, track.LengthSectors);
        Assert.Equal(330_000, toc.LeadOutLba);
        Assert.Equal(DiscContent.Data, toc.Content);
        Assert.False(toc.IsMultiSession);
        Assert.Equal(SessionDiscType.CdDaOrCdRom, session.DiscType);
    }

    [Fact]
    public void AudioDiscHasNoDataTracks()
    {
        var toc = TocParser.ParseFull(OpticalTestData.FullToc(
            (1, [(1, false, 0), (2, false, 20_000), (3, false, 41_000)], 60_000, 0x00)));

        Assert.NotNull(toc);
        Assert.Equal(DiscContent.Audio, toc.Content);
        Assert.Equal(3, toc.Tracks.Count());
        Assert.Equal([20_000, 21_000, 19_000], toc.Tracks.Select(t => t.LengthSectors));
    }

    [Fact]
    public void DataTrackFollowedByAudioIsMixedMode()
    {
        var toc = TocParser.ParseFull(OpticalTestData.FullToc(
            (1, [(1, true, 0), (2, false, 10_000), (3, false, 30_000)], 50_000, 0x00)));

        Assert.NotNull(toc);
        Assert.Equal(DiscContent.Mixed, toc.Content);
        Assert.True(toc.HasData);
        Assert.True(toc.HasAudio);
        Assert.False(toc.IsMultiSession);
    }

    [Fact]
    public void AudioSessionPlusDataSessionIsMixedAndMultiSession()
    {
        // an Enhanced CD: audio in session 1, the data session after the gap
        var toc = TocParser.ParseFull(OpticalTestData.FullToc(
            (1, [(1, false, 0), (2, false, 20_000)], 40_000, 0x00),
            (2, [(3, true, 51_000)], 70_000, 0x20)));

        Assert.NotNull(toc);
        Assert.Equal(DiscContent.Mixed, toc.Content);
        Assert.True(toc.IsMultiSession);
        Assert.Equal(2, toc.Sessions.Count);
        Assert.Equal(SessionDiscType.CdRomXa, toc.Sessions[1].DiscType);
        Assert.Equal(70_000, toc.LeadOutLba);
        Assert.Equal(2, toc.Sessions[1].Number);
        Assert.Equal(3, toc.Sessions[1].Tracks[0].Number);
        Assert.Equal(2, toc.Sessions[1].Tracks[0].Session);
    }

    [Fact]
    public void TwoDataSessionsAreMultiSessionButStillData()
    {
        var toc = TocParser.ParseFull(OpticalTestData.FullToc(
            (1, [(1, true, 0)], 20_000, 0x00),
            (2, [(2, true, 31_000)], 40_000, 0x00)));

        Assert.NotNull(toc);
        Assert.True(toc.IsMultiSession);
        Assert.Equal(DiscContent.Data, toc.Content);
    }

    [Fact]
    public void LastTrackOfAnEarlierSessionEndsAtItsOwnLeadOut()
    {
        var toc = TocParser.ParseFull(OpticalTestData.FullToc(
            (1, [(1, true, 0)], 20_000, 0x00),
            (2, [(2, true, 31_000)], 40_000, 0x00)));

        Assert.NotNull(toc);
        Assert.Equal(20_000, toc.Sessions[0].Tracks[0].LengthSectors);
        Assert.Equal(9_000, toc.Sessions[1].Tracks[0].LengthSectors);
    }

    [Fact]
    public void CopyPermittedBitIsRead()
    {
        var data = OpticalTestData.FullToc((1, [(1, true, 0)], 1000, 0x00));
        // control nibble of the track descriptor: data (4) plus copy permitted (2)
        for (var i = 4; i + 11 <= data.Length; i += 11)
        {
            if (data[i + 3] == 1)
            {
                data[i + 1] = 0x16;
            }
        }

        var toc = TocParser.ParseFull(data);

        Assert.True(toc!.Tracks.Single().CopyPermitted);
    }

    [Fact]
    public void DescriptorsOtherThanPositionDataAreIgnored()
    {
        var data = OpticalTestData.FullToc((1, [(1, true, 0)], 1000, 0x00));
        // a multi-session pointer (ADR 5) that must not be taken for a track
        var extended = data.Concat(new byte[] { 1, 0x54, 0, 0xB0, 1, 2, 3, 0, 4, 5, 6 }).ToArray();
        extended[0] = (byte)((extended.Length - 2) >> 8);
        extended[1] = (byte)(extended.Length - 2);

        var toc = TocParser.ParseFull(extended);

        Assert.Single(toc!.Tracks);
    }

    [Fact]
    public void RepeatedDescriptorsDoNotDuplicateTracks()
    {
        var once = OpticalTestData.FullToc((1, [(1, true, 0)], 1000, 0x00));
        var twice = once.Concat(once.Skip(4)).ToArray();
        twice[0] = (byte)((twice.Length - 2) >> 8);
        twice[1] = (byte)(twice.Length - 2);

        Assert.Single(TocParser.ParseFull(twice)!.Tracks);
    }

    [Fact]
    public void DeclaredLengthLargerThanTheDataIsTolerated()
    {
        var data = OpticalTestData.FullToc((1, [(1, true, 0)], 1000, 0x00));
        data[0] = 0xFF;

        Assert.NotNull(TocParser.ParseFull(data));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void TooShortDataGivesNoToc(int length)
    {
        Assert.Null(TocParser.ParseFull(new byte[length]));
        Assert.Null(TocParser.ParseBasic(new byte[length], addressesAreMsf: false));
    }

    [Fact]
    public void BasicTocWithLbaAddresses()
    {
        var toc = TocParser.ParseBasic(OpticalTestData.BasicToc([(1, true, 0), (2, false, 5000)], 9000), addressesAreMsf: false);

        Assert.NotNull(toc);
        Assert.Equal(DiscContent.Mixed, toc.Content);
        Assert.Equal(9000, toc.LeadOutLba);
        Assert.Equal([5000, 4000], toc.Tracks.Select(t => t.LengthSectors));
        Assert.All(toc.Tracks, t => Assert.Equal(1, t.Session));
    }

    [Fact]
    public void BasicTocWithMsfAddresses()
    {
        var data = new byte[4 + 2 * 8];
        data[1] = 18;
        data[2] = 1;
        data[3] = 1;
        data[4 + 1] = 0x14;
        data[4 + 2] = 1;
        data[4 + 5] = 0;
        data[4 + 6] = 2;
        data[4 + 7] = 0;
        data[12 + 1] = 0x14;
        data[12 + 2] = 0xAA;
        data[12 + 5] = 60;
        data[12 + 6] = 0;
        data[12 + 7] = 0;

        var toc = TocParser.ParseBasic(data, addressesAreMsf: true);

        Assert.NotNull(toc);
        Assert.Equal(0, toc.Tracks.Single().StartLba);
        Assert.Equal(60 * 60 * 75 - 150, toc.LeadOutLba);
    }

    [Fact]
    public void BasicTocWithoutLeadOutIsRejected()
    {
        var data = OpticalTestData.BasicToc([(1, true, 0)], 1000);
        // drop the lead-out descriptor (last 8 bytes)
        var cut = data[..^8];
        cut[0] = (byte)((cut.Length - 2) >> 8);
        cut[1] = (byte)(cut.Length - 2);

        Assert.Null(TocParser.ParseBasic(cut, addressesAreMsf: false));
    }
}
