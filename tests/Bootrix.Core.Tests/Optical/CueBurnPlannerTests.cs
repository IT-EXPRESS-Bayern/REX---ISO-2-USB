// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Tests.Optical.Support;

namespace Bootrix.Core.Tests.Optical;

public sealed class CueBurnPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-cue-" + Guid.NewGuid().ToString("N"));

    public CueBurnPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Func<string, long?> Lengths(long length) => _ => length;

    private static CueDataPlan Plan(string cue, long binLength = 2352L * 1000) =>
        CueBurnPlanner.PlanDataDisc(CueParser.Parse(cue), "/discs", Lengths(binLength));

    [Fact]
    public void SingleRawMode1TrackCanBeBurned()
    {
        var plan = Plan("FILE \"game.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n");

        Assert.Equal(CueTrackMode.Mode1Raw2352, plan.Mode);
        Assert.Equal(0, plan.StartSector);
        Assert.Equal(1000, plan.SectorCount);
        Assert.Equal(1000 * 2048L, plan.UserDataBytes);
        Assert.EndsWith("game.bin", plan.BinPath);
    }

    [Fact]
    public void CookedMode1TrackUsesTheSmallerSectorSize()
    {
        var plan = Plan("FILE \"data.iso\" BINARY\nTRACK 01 MODE1/2048\nINDEX 01 00:00:00\n", binLength: 2048L * 500);

        Assert.Equal(CueTrackMode.Mode1Data2048, plan.Mode);
        Assert.Equal(500, plan.SectorCount);
    }

    [Fact]
    public void ImageThatStoresItsPregapStartsAtIndexOne()
    {
        // the pregap data (INDEX 00) is in the file; the disc image begins at INDEX 01
        var plan = Plan("FILE \"game.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 00 00:00:00\nINDEX 01 00:02:00\n");

        Assert.Equal(150, plan.StartSector);
        Assert.Equal(850, plan.SectorCount);
    }

    [Theory]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 AUDIO\nINDEX 01 00:00:00\n", "audio")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\nTRACK 02 AUDIO\nINDEX 01 10:00:00\n", "audio")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 MODE2/2352\nINDEX 01 00:00:00\n", "MODE2")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 MODE2/2336\nINDEX 01 00:00:00\n", "MODE2")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 CDI/2352\nINDEX 01 00:00:00\n", "CD-I")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 CDG\nINDEX 01 00:00:00\n", "CD+G")]
    [InlineData("FILE \"a.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\nTRACK 02 MODE1/2352\nINDEX 01 05:00:00\n", "2 tracks")]
    [InlineData("FILE \"a.wav\" WAVE\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n", "WAVE")]
    public void ImagesThatAreNotASingleMode1TrackAreRefused(string cue, string reasonFragment)
    {
        var ex = Assert.Throws<BootrixException>(() => Plan(cue));

        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);
        Assert.Contains(reasonFragment, ex.Detail);
    }

    [Fact]
    public void MissingBinFileIsReported()
    {
        var sheet = CueParser.Parse("FILE \"gone.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n");

        var ex = Assert.Throws<BootrixException>(() => CueBurnPlanner.PlanDataDisc(sheet, "/discs", _ => null));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [Fact]
    public void BinShorterThanItsPregapHasNoData()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            Plan("FILE \"a.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:02:00\n", binLength: 2352L * 100));

        Assert.Equal(ErrorCode.ImageTruncated, ex.Code);
    }

    [Fact]
    public void FileNamesUseWindowsSeparatorsInTheSheet()
    {
        var plan = Plan("FILE \"sub\\game.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n");

        Assert.Contains("game.bin", plan.BinPath);
    }

    [Fact]
    public void UserDataIsTakenFromTheRawSectors()
    {
        var iso = OpticalTestData.DiscImage(200, seed: 5);
        var bin = Path.Combine(_dir, "game.bin");
        File.WriteAllBytes(bin, RawBin.FromIso(iso));
        File.WriteAllText(Path.Combine(_dir, "game.cue"), "FILE \"game.bin\" BINARY\r\n  TRACK 01 MODE1/2352\r\n    INDEX 01 00:00:00\r\n");

        var source = CueBurnPlanner.FromFile(Path.Combine(_dir, "game.cue"));

        Assert.Equal(DiscImageKind.BinCue, source.Kind);
        Assert.Equal(iso.Length, source.LengthBytes);
        using var stream = source.OpenStream();
        var converted = new byte[iso.Length];
        stream.ReadExactly(converted);
        Assert.Equal(SHA256.HashData(iso), SHA256.HashData(converted));
    }

    [Fact]
    public void ImageWithPregapInTheFileDropsThePregap()
    {
        var iso = OpticalTestData.DiscImage(100, seed: 6);
        var bin = Path.Combine(_dir, "pre.bin");
        File.WriteAllBytes(bin, RawBin.FromIso(iso, pregapSectors: 150));
        File.WriteAllText(Path.Combine(_dir, "pre.cue"), "FILE \"pre.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 00 00:00:00\nINDEX 01 00:02:00\n");

        var source = CueBurnPlanner.FromFile(Path.Combine(_dir, "pre.cue"));
        using var stream = source.OpenStream();
        var converted = new byte[iso.Length];
        stream.ReadExactly(converted);

        Assert.Equal(iso, converted);
    }

    [Fact]
    public void MislabelledBinIsCaughtBeforeBurning()
    {
        // the sheet says MODE1/2352 but the file holds plain 2048-byte sectors, e.g. a renamed ISO
        var bin = Path.Combine(_dir, "fake.bin");
        File.WriteAllBytes(bin, new byte[2352 * 20]);
        File.WriteAllText(Path.Combine(_dir, "fake.cue"), "FILE \"fake.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n");

        var ex = Assert.Throws<BootrixException>(() => CueBurnPlanner.FromFile(Path.Combine(_dir, "fake.cue")));

        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);
    }

    [Fact]
    public void Mode2SectorIsNotMistakenForMode1()
    {
        var raw = RawBin.FromIso(new byte[2048]);
        raw[15] = 2;

        Assert.False(Mode1UserDataStream.IsMode1Sector(raw));
        raw[15] = 1;
        Assert.True(Mode1UserDataStream.IsMode1Sector(raw));
        raw[3] = 0;
        Assert.False(Mode1UserDataStream.IsMode1Sector(raw));
    }

    [Fact]
    public void StreamSeeksToAnyOffset()
    {
        var iso = OpticalTestData.DiscImage(64, seed: 7);
        using var stream = new Mode1UserDataStream(new MemoryStream(RawBin.FromIso(iso)), rawSectors: true, startSector: 0, sectorCount: 64);

        Assert.Equal(64 * 2048, stream.Length);
        stream.Seek(30 * 2048 + 100, SeekOrigin.Begin);
        var piece = new byte[5000];
        stream.ReadExactly(piece);

        Assert.Equal(iso.AsSpan(30 * 2048 + 100, 5000).ToArray(), piece);
        Assert.Equal(30 * 2048 + 100 + 5000, stream.Position);
    }

    [Fact]
    public void ReadingPastTheEndReturnsNothing()
    {
        using var stream = new Mode1UserDataStream(new MemoryStream(RawBin.FromIso(new byte[2048 * 4])), rawSectors: true, startSector: 0, sectorCount: 4);
        stream.Seek(0, SeekOrigin.End);

        Assert.Equal(0, stream.Read(new byte[10]));
    }

    [Fact]
    public void PassThroughStreamServesCookedSectors()
    {
        var iso = OpticalTestData.DiscImage(40, seed: 8);
        using var stream = new Mode1UserDataStream(new MemoryStream(iso), rawSectors: false, startSector: 4, sectorCount: 36);

        var data = new byte[36 * 2048];
        stream.ReadExactly(data);

        Assert.Equal(iso.AsSpan(4 * 2048).ToArray(), data);
    }

    [NeedsToolFact("xorriso", "isoinfo")]
    public void ConvertedRealIsoStillHasItsFiles()
    {
        var tree = Path.Combine(_dir, "tree");
        Directory.CreateDirectory(Path.Combine(tree, "docs"));
        File.WriteAllText(Path.Combine(tree, "readme.txt"), "hello disc");
        var payload = new byte[70_000];
        new Random(3).NextBytes(payload);
        File.WriteAllBytes(Path.Combine(tree, "docs", "data.bin"), payload);
        var iso = Path.Combine(_dir, "real.iso");
        var (code, output) = OpticalTools.Run("xorriso", "-as", "mkisofs", "-quiet", "-V", "BINCUE", "-o", iso, tree);
        Assert.True(code == 0, output);

        File.WriteAllBytes(Path.Combine(_dir, "real.bin"), RawBin.FromIso(File.ReadAllBytes(iso)));
        File.WriteAllText(Path.Combine(_dir, "real.cue"), "FILE \"real.bin\" BINARY\nTRACK 01 MODE1/2352\nINDEX 01 00:00:00\n");

        var source = CueBurnPlanner.FromFile(Path.Combine(_dir, "real.cue"));
        var back = Path.Combine(_dir, "back.iso");
        using (var input = source.OpenStream())
        using (var output2 = File.Create(back))
        {
            input.CopyTo(output2);
        }

        var (listCode, listing) = OpticalTools.Run("isoinfo", "-l", "-i", back);
        Assert.True(listCode == 0, listing);
        Assert.Contains("README.TXT", listing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DATA.BIN", listing, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(iso)), SHA256.HashData(File.ReadAllBytes(back)));
    }
}
