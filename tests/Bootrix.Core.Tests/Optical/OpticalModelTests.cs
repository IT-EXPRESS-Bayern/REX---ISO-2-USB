// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;

namespace Bootrix.Core.Tests.Optical;

public class OpticalModelTests
{
    [Theory]
    [InlineData(OpticalMediaFamily.Cd, 75, 1)]
    [InlineData(OpticalMediaFamily.Cd, 75 * 48, 48)]
    [InlineData(OpticalMediaFamily.Dvd, 680 * 16, 16)]
    [InlineData(OpticalMediaFamily.Dvd, 676, 1)]
    [InlineData(OpticalMediaFamily.BluRay, 2195 * 12, 12)]
    [InlineData(OpticalMediaFamily.Unknown, 1000, 0)]
    [InlineData(OpticalMediaFamily.Cd, 0, 0)]
    public void SpeedFactorFollowsTheImapiBaseRates(OpticalMediaFamily family, int sectorsPerSecond, int factor)
    {
        Assert.Equal(factor, SectorMath.SpeedFactor(family, sectorsPerSecond));
    }

    [Theory]
    [InlineData(OpticalMediaFamily.Cd, 16, 1200)]
    [InlineData(OpticalMediaFamily.Dvd, 8, 5440)]
    [InlineData(OpticalMediaFamily.BluRay, 4, 8780)]
    [InlineData(OpticalMediaFamily.Dvd, null, -1)]
    [InlineData(OpticalMediaFamily.Dvd, 0, -1)]
    public void RequestedFactorBecomesSectorsPerSecond(OpticalMediaFamily family, int? factor, int expected)
    {
        Assert.Equal(expected, SectorMath.ToSectorsPerSecond(family, factor));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2048, 1)]
    [InlineData(2049, 2)]
    [InlineData(4_700_000_000, 2_294_922)]
    public void BytesRoundUpToWholeSectors(long bytes, long sectors)
    {
        Assert.Equal(sectors, SectorMath.SectorsFor(bytes));
        Assert.Equal(sectors * 2048, SectorMath.RoundUpToSector(bytes));
    }

    [Theory]
    [InlineData(OpticalMediaType.CdR, OpticalMediaFamily.Cd, false, true)]
    [InlineData(OpticalMediaType.CdRw, OpticalMediaFamily.Cd, true, true)]
    [InlineData(OpticalMediaType.DvdPlusRDualLayer, OpticalMediaFamily.Dvd, false, true)]
    [InlineData(OpticalMediaType.DvdRam, OpticalMediaFamily.Dvd, true, true)]
    [InlineData(OpticalMediaType.BdRe, OpticalMediaFamily.BluRay, true, true)]
    [InlineData(OpticalMediaType.BdRom, OpticalMediaFamily.BluRay, false, false)]
    [InlineData(OpticalMediaType.HdDvdR, OpticalMediaFamily.Unknown, false, false)]
    [InlineData(OpticalMediaType.Unknown, OpticalMediaFamily.Unknown, false, false)]
    public void MediaTypesKnowTheirFamilyAndWritability(OpticalMediaType type, OpticalMediaFamily family, bool rewritable, bool writable)
    {
        Assert.Equal(family, type.Family());
        Assert.Equal(rewritable, type.IsRewritable());
        Assert.Equal(writable, type.IsWritable());
    }

    [Fact]
    public void MediaTypeNumbersMatchImapi()
    {
        // IMAPI_MEDIA_PHYSICAL_TYPE; BD-R is 0x12 and BD-RE 0x13 according to the IMAPI documentation.
        Assert.Equal(0x02, (int)OpticalMediaType.CdR);
        Assert.Equal(0x06, (int)OpticalMediaType.DvdPlusR);
        Assert.Equal(0x12, (int)OpticalMediaType.BdR);
        Assert.Equal(0x13, (int)OpticalMediaType.BdRe);
    }

    [Fact]
    public void MmcProfilesBecomeCapabilities()
    {
        var capabilities = OpticalCapabilitiesExtensions.FromMmcProfiles([0x09, 0x0A, 0x1B, 0x1A, 0x2B, 0x11, 0x14, 0x13, 0x43, 0x08, 0x10, 0x40]);

        Assert.Equal(
            OpticalCapabilities.CdR | OpticalCapabilities.CdRw | OpticalCapabilities.DvdPlusR | OpticalCapabilities.DvdPlusRw
            | OpticalCapabilities.DvdPlusRDualLayer | OpticalCapabilities.DvdMinusR | OpticalCapabilities.DvdMinusRw | OpticalCapabilities.BdRe,
            capabilities);
    }

    [Theory]
    [InlineData(0x41, OpticalCapabilities.BdR)]
    [InlineData(0x42, OpticalCapabilities.BdR)]
    [InlineData(0x15, OpticalCapabilities.DvdMinusRDualLayer)]
    [InlineData(0x16, OpticalCapabilities.DvdMinusRDualLayer)]
    [InlineData(0x12, OpticalCapabilities.DvdRam)]
    [InlineData(0x2A, OpticalCapabilities.DvdPlusRwDualLayer)]
    [InlineData(0x08, OpticalCapabilities.None)]
    [InlineData(0x50, OpticalCapabilities.None)]
    public void IndividualProfilesMap(int profile, OpticalCapabilities expected)
    {
        Assert.Equal(expected, OpticalCapabilitiesExtensions.FromMmcProfile(profile));
    }

    [Fact]
    public void CapabilitiesDecideWhichDiscsCanBeWritten()
    {
        var dvdWriter = OpticalCapabilities.CdR | OpticalCapabilities.CdRw | OpticalCapabilities.DvdPlusR | OpticalCapabilities.DvdMinusR;

        Assert.True(dvdWriter.CanWrite(OpticalMediaType.DvdPlusR));
        Assert.True(dvdWriter.CanWrite(OpticalMediaType.CdRw));
        Assert.False(dvdWriter.CanWrite(OpticalMediaType.BdR));
        Assert.False(dvdWriter.CanWrite(OpticalMediaType.DvdRom));
        Assert.False(OpticalCapabilities.None.CanWrite(OpticalMediaType.CdR));
    }

    [Fact]
    public void EveryWritableMediaTypeHasExactlyOneCapability()
    {
        foreach (var type in Enum.GetValues<OpticalMediaType>().Where(t => t.IsWritable()))
        {
            var capability = OpticalCapabilitiesExtensions.FromMediaType(type);

            Assert.NotEqual(OpticalCapabilities.None, capability);
            Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)capability));
            Assert.True(capability.CanWrite(type));
        }

        Assert.Equal(OpticalCapabilities.None, OpticalCapabilitiesExtensions.FromMediaType(OpticalMediaType.BdRom));
        Assert.Equal(OpticalCapabilities.None, OpticalCapabilitiesExtensions.FromMediaType(OpticalMediaType.HdDvdR));
    }

    [Fact]
    public void DriveNameCombinesVendorAndProduct()
    {
        var drive = new OpticalDrive { Id = "x", Vendor = "HL-DT-ST ", Product = "DVDRAM GH24NSD1", DriveLetter = "E:" };

        Assert.Equal("HL-DT-ST DVDRAM GH24NSD1", drive.Name);
        Assert.Equal("E: HL-DT-ST DVDRAM GH24NSD1", drive.DisplayName);
        Assert.False(drive.CanRecord);
    }

    private static OpticalMedia Disc(OpticalMediaType type, OpticalMediaState state, bool heuristicallyBlank = false, bool supported = true) => new()
    {
        Type = type,
        State = state,
        HeuristicallyBlank = heuristicallyBlank,
        IsSupported = supported,
        TotalSectors = 2_295_104,
        FreeSectors = 2_295_104,
    };

    [Fact]
    public void NoDiscMeansNoMedia()
    {
        Assert.Equal(OpticalMediaCondition.NoMedia, OpticalMedia.None.Condition);
    }

    [Fact]
    public void BlankRecordableDiscIsBlank()
    {
        var media = Disc(OpticalMediaType.DvdPlusR, OpticalMediaState.Blank);

        Assert.Equal(OpticalMediaCondition.Blank, media.Condition);
        Assert.True(media.IsBlank);
        Assert.Equal(2_295_104L * 2048, media.CapacityBytes);
    }

    [Fact]
    public void OverwritableDiscThatLooksEmptyCountsAsBlank()
    {
        // DVD+RW never reports the blank flag; IMAPI only knows it by looking at the content.
        var media = Disc(OpticalMediaType.DvdPlusRw, OpticalMediaState.OverwriteOnly, heuristicallyBlank: true);

        Assert.Equal(OpticalMediaCondition.Blank, media.Condition);
    }

    [Fact]
    public void WrittenRewritableDiscNeedsErasing()
    {
        var media = Disc(OpticalMediaType.CdRw, OpticalMediaState.NonEmptySession | OpticalMediaState.EraseRequired);

        Assert.Equal(OpticalMediaCondition.Rewritable, media.Condition);
        Assert.True(media.IsRewritable);
    }

    [Fact]
    public void OpenWriteOnceDiscCanBeAppended()
    {
        var media = Disc(OpticalMediaType.CdR, OpticalMediaState.Appendable | OpticalMediaState.NonEmptySession);

        Assert.Equal(OpticalMediaCondition.Appendable, media.Condition);
        Assert.True(media.IsMultiSession);
    }

    [Fact]
    public void FinalizedWriteOnceDiscIsClosed()
    {
        var media = Disc(OpticalMediaType.DvdMinusR, OpticalMediaState.Finalized | OpticalMediaState.NonEmptySession);

        Assert.Equal(OpticalMediaCondition.Closed, media.Condition);
    }

    [Theory]
    [InlineData(OpticalMediaType.CdRom, OpticalMediaState.Finalized)]
    [InlineData(OpticalMediaType.BdRom, OpticalMediaState.Finalized)]
    [InlineData(OpticalMediaType.DvdPlusR, OpticalMediaState.Blank | OpticalMediaState.WriteProtected)]
    [InlineData(OpticalMediaType.DvdPlusR, OpticalMediaState.Damaged)]
    [InlineData(OpticalMediaType.HdDvdR, OpticalMediaState.Blank)]
    public void DiscsThatCannotBeWrittenAreReportedAsSuch(OpticalMediaType type, OpticalMediaState state)
    {
        Assert.Equal(OpticalMediaCondition.NotWritable, Disc(type, state).Condition);
    }

    [Fact]
    public void MediaTheRecorderDoesNotSupportIsNotWritable()
    {
        Assert.Equal(OpticalMediaCondition.NotWritable, Disc(OpticalMediaType.BdR, OpticalMediaState.Blank, supported: false).Condition);
    }

    [Fact]
    public void MaximumSpeedComesFromTheSpeedList()
    {
        var media = Disc(OpticalMediaType.DvdMinusR, OpticalMediaState.Blank) with
        {
            WriteSpeeds = [new WriteSpeed(680 * 4, false), new WriteSpeed(680 * 16, false), new WriteSpeed(680 * 8, true)],
        };

        Assert.Equal(16, media.MaxWriteSpeedFactor);
        Assert.Equal(0, OpticalMedia.None.MaxWriteSpeedFactor);
    }

    [Fact]
    public void StateBitsMatchImapi()
    {
        // IMAPI_FORMAT2_DATA_MEDIA_STATE
        Assert.Equal(0x2, (int)OpticalMediaState.Blank);
        Assert.Equal(0x4, (int)OpticalMediaState.Appendable);
        Assert.Equal(0x800, (int)OpticalMediaState.EraseRequired);
        Assert.Equal(0x2000, (int)OpticalMediaState.WriteProtected);
        Assert.Equal(0xFC00, (int)OpticalMediaState.UnsupportedMask);
    }

    [Fact]
    public void OptionsRejectSectorSizesOtherThan2048()
    {
        var ex = Assert.Throws<BootrixException>(() => new BurnOptions { SectorSize = 2352 }.Validate());

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
        new BurnOptions().Validate();
    }

    [Fact]
    public void OptionsRejectNonsensicalSpeed()
    {
        Assert.Throws<BootrixException>(() => new BurnOptions { WriteSpeedFactor = 0 }.Validate());
        new BurnOptions { WriteSpeedFactor = 16 }.Validate();
    }
}
