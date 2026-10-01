// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;

namespace Bootrix.Core.Tests.Optical;

public class CueParserTests
{
    [Fact]
    public void SingleDataTrack()
    {
        var sheet = CueParser.Parse("""
            FILE "game.bin" BINARY
              TRACK 01 MODE1/2352
                INDEX 01 00:00:00
            """);

        var file = Assert.Single(sheet.Files);
        Assert.Equal("game.bin", file.Name);
        Assert.True(file.IsBinary);
        var track = Assert.Single(sheet.Tracks);
        Assert.Equal(1, track.Number);
        Assert.Equal(CueTrackMode.Mode1Raw2352, track.Mode);
        Assert.Equal(new Msf(0, 0, 0), track.Start!.Position);
        Assert.Same(file, track.File);
    }

    [Fact]
    public void AudioDiscWithOneFilePerTrack()
    {
        var sheet = CueParser.Parse("""
            REM GENRE Rock
            REM DATE 1999
            CATALOG 0123456789012
            PERFORMER "The Band"
            TITLE "The Album"
            FILE "01 - First.bin" BINARY
              TRACK 01 AUDIO
                TITLE "First"
                PERFORMER "The Band"
                FLAGS DCP PRE
                INDEX 01 00:00:00
            FILE "02 - Second.bin" BINARY
              TRACK 02 AUDIO
                PREGAP 00:02:00
                POSTGAP 00:01:00
                INDEX 00 00:00:00
                INDEX 01 00:01:30
            """);

        Assert.Equal(2, sheet.Files.Count);
        Assert.Equal("0123456789012", sheet.Catalog);
        Assert.Equal("The Album", sheet.Title);
        Assert.Equal("The Band", sheet.Performer);

        var first = sheet.Tracks[0];
        Assert.Equal("First", first.Title);
        Assert.Equal(["DCP", "PRE"], first.Flags);
        Assert.Equal("01 - First.bin", first.File.Name);

        var second = sheet.Tracks[1];
        Assert.Equal("02 - Second.bin", second.File.Name);
        Assert.Equal(new Msf(0, 2, 0), second.Pregap);
        Assert.Equal(new Msf(0, 1, 0), second.Postgap);
        Assert.Equal(new Msf(0, 0, 0), second.PregapIndex!.Position);
        Assert.Equal(new Msf(0, 1, 30), second.Start!.Position);
        Assert.Equal(105, second.Start.Position.ToFrames());
    }

    [Fact]
    public void MixedModeDiscInOneFile()
    {
        var sheet = CueParser.Parse("""
            FILE "disc.bin" BINARY
              TRACK 01 MODE2/2352
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 00 58:51:25
                INDEX 01 58:53:25
            """);

        Assert.Single(sheet.Files);
        Assert.Equal([CueTrackMode.Mode2Raw2352, CueTrackMode.Audio], sheet.Tracks.Select(t => t.Mode));
        Assert.Equal(new Msf(58, 53, 25), sheet.Tracks[1].Start!.Position);
    }

    [Theory]
    [InlineData("MODE1/2048", CueTrackMode.Mode1Data2048, 2048)]
    [InlineData("MODE1/2352", CueTrackMode.Mode1Raw2352, 2352)]
    [InlineData("MODE2/2336", CueTrackMode.Mode2Data2336, 2336)]
    [InlineData("MODE2/2352", CueTrackMode.Mode2Raw2352, 2352)]
    [InlineData("AUDIO", CueTrackMode.Audio, 2352)]
    [InlineData("CDG", CueTrackMode.CdGraphics2448, 2448)]
    [InlineData("CDI/2336", CueTrackMode.CdiData2336, 2336)]
    [InlineData("CDI/2352", CueTrackMode.CdiRaw2352, 2352)]
    [InlineData("mode1/2352", CueTrackMode.Mode1Raw2352, 2352)]
    public void TrackTypesKnowTheirSectorSize(string text, CueTrackMode mode, int sectorSize)
    {
        var sheet = CueParser.Parse($"FILE \"a.bin\" BINARY\nTRACK 01 {text}\nINDEX 01 00:00:00\n");

        Assert.Equal(mode, sheet.Tracks[0].Mode);
        Assert.Equal(sectorSize, mode.SectorSize());
    }

    [Fact]
    public void CommandsAreCaseInsensitiveAndLineEndingsDoNotMatter()
    {
        var sheet = CueParser.Parse("﻿file \"x.bin\" binary\r\n  track 1 mode1/2352\r\n    index 01 00:00:00\r\n");

        Assert.Equal("BINARY", sheet.Files[0].Type);
        Assert.Single(sheet.Tracks);
    }

    [Fact]
    public void FileNamesWithoutQuotesMayContainSpaces()
    {
        var sheet = CueParser.Parse("FILE My Game Disc.bin BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n");

        Assert.Equal("My Game Disc.bin", sheet.Files[0].Name);
        Assert.Equal("BINARY", sheet.Files[0].Type);
    }

    [Fact]
    public void FileTypeDefaultsToBinary()
    {
        var sheet = CueParser.Parse("FILE \"x.bin\"\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n");

        Assert.Equal("BINARY", sheet.Files[0].Type);
    }

    [Fact]
    public void WaveFilesAreRecognisedAsNotBinary()
    {
        var sheet = CueParser.Parse("FILE \"x.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00\n");

        Assert.False(sheet.Files[0].IsBinary);
    }

    [Theory]
    [InlineData("TRACK 01 MODE1/2352\nINDEX 01 00:00:00\n", "TRACK before any FILE")]
    [InlineData("FILE \"a.bin\" BINARY\nINDEX 01 00:00:00\n", "INDEX before any TRACK")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 MODE1/2352\n", "no INDEX 01")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:61:00\n", "mm:ss:ff")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 MODE9/2352\nINDEX 01 00:00:00\n", "unknown track type")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 00 MODE1/2352\nINDEX 01 00:00:00\n", "number from 1 to 99")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\nTRACK 01 AUDIO\nINDEX 01 00:00:00\n", "appears twice")]
    [InlineData("FILE\n", "file name")]
    [InlineData("REM nothing here\n", "no TRACK")]
    [InlineData("", "no TRACK")]
    public void BrokenSheetsAreRejectedWithTheReason(string text, string expectedFragment)
    {
        var ex = Assert.Throws<BootrixException>(() => CueParser.Parse(text));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
        Assert.Contains(expectedFragment, ex.Detail);
    }

    [Fact]
    public void ErrorsNameTheLine()
    {
        var ex = Assert.Throws<BootrixException>(() => CueParser.Parse("FILE \"a.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 xx\n"));

        Assert.Contains("line 3", ex.Detail);
    }

    [Fact]
    public void UnknownCommandsAreIgnored()
    {
        var sheet = CueParser.Parse("CDTEXTFILE \"x.cdt\"\nFILE \"a.bin\" BINARY\nTRACK 01 MODE1/2352\nSONGWRITER \"x\"\nISRC ABCDE1234567\nINDEX 01 00:00:00\n");

        Assert.Single(sheet.Tracks);
    }

    [Fact]
    public void TokenizerKeepsQuotedText()
    {
        Assert.Equal(["FILE", "a b.bin", "BINARY"], CueParser.Tokenize("FILE \"a b.bin\" BINARY"));
        Assert.Equal(["REM", "x"], CueParser.Tokenize("  REM   x  "));
        Assert.Equal(["TITLE", ""], CueParser.Tokenize("TITLE \"\""));
    }

    [Fact]
    public void FilesInLatin1AreReadByFileName()
    {
        var path = Path.Combine(Path.GetTempPath(), "bootrix-cue-" + Guid.NewGuid().ToString("N") + ".cue");
        try
        {
            File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes("FILE \"Händel.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n"));

            Assert.Equal("Händel.bin", CueParser.ParseFile(path).Files[0].Name);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
