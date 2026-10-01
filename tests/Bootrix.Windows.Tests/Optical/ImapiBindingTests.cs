// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical;
using Bootrix.Windows.Optical;
using Windows.Win32;
using Windows.Win32.Devices.Dvd;
using Windows.Win32.Storage.Imapi;

namespace Bootrix.Windows.Tests.Optical;

/// <summary>
/// The Core model mirrors several IMAPI enumerations so the Windows layer can cast instead of translate. These
/// tests compare the numbers with the generated bindings, which come from the Win32 metadata and so are the
/// reference; none of this needs a drive or even Windows.
/// </summary>
public class ImapiBindingTests
{
    [Fact]
    public void MediaStateBitsMatchTheBindings()
    {
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_BLANK, (int)OpticalMediaState.Blank);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_APPENDABLE, (int)OpticalMediaState.Appendable);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_FINAL_SESSION, (int)OpticalMediaState.FinalSession);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_OVERWRITE_ONLY, (int)OpticalMediaState.OverwriteOnly);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_DAMAGED, (int)OpticalMediaState.Damaged);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_ERASE_REQUIRED, (int)OpticalMediaState.EraseRequired);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_NON_EMPTY_SESSION, (int)OpticalMediaState.NonEmptySession);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_WRITE_PROTECTED, (int)OpticalMediaState.WriteProtected);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_FINALIZED, (int)OpticalMediaState.Finalized);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_UNSUPPORTED_MEDIA, (int)OpticalMediaState.UnsupportedMedia);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_MEDIA_STATE.IMAPI_FORMAT2_DATA_MEDIA_STATE_UNSUPPORTED_MASK, (int)OpticalMediaState.UnsupportedMask);
    }

    [Theory]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_UNKNOWN, OpticalMediaType.Unknown)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_CDROM, OpticalMediaType.CdRom)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_CDR, OpticalMediaType.CdR)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_CDRW, OpticalMediaType.CdRw)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDROM, OpticalMediaType.DvdRom)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDRAM, OpticalMediaType.DvdRam)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDPLUSR, OpticalMediaType.DvdPlusR)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDPLUSRW, OpticalMediaType.DvdPlusRw)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDPLUSR_DUALLAYER, OpticalMediaType.DvdPlusRDualLayer)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDDASHR, OpticalMediaType.DvdMinusR)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDDASHRW, OpticalMediaType.DvdMinusRw)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDDASHR_DUALLAYER, OpticalMediaType.DvdMinusRDualLayer)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DISK, OpticalMediaType.Disk)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_DVDPLUSRW_DUALLAYER, OpticalMediaType.DvdPlusRwDualLayer)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_HDDVDROM, OpticalMediaType.HdDvdRom)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_HDDVDR, OpticalMediaType.HdDvdR)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_HDDVDRAM, OpticalMediaType.HdDvdRam)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_BDROM, OpticalMediaType.BdRom)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_BDR, OpticalMediaType.BdR)]
    [InlineData((int)IMAPI_MEDIA_PHYSICAL_TYPE.IMAPI_MEDIA_TYPE_BDRE, OpticalMediaType.BdRe)]
    public void MediaTypesMatchTheBindings(int imapi, OpticalMediaType ours)
    {
        Assert.Equal(imapi, (int)ours);
    }

    [Fact]
    public void WriteActionsMatchTheBindings()
    {
        Assert.Equal((int)IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_VALIDATING_MEDIA, (int)BurnPhase.ValidatingMedia);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_FORMATTING_MEDIA, (int)BurnPhase.FormattingMedia);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_INITIALIZING_HARDWARE, (int)BurnPhase.InitializingHardware);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_CALIBRATING_POWER, (int)BurnPhase.CalibratingPower);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_WRITING_DATA, (int)BurnPhase.Writing);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_FINALIZATION, (int)BurnPhase.Finalizing);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_COMPLETED, (int)BurnPhase.Completed);
        Assert.Equal((int)IMAPI_FORMAT2_DATA_WRITE_ACTION.IMAPI_FORMAT2_DATA_WRITE_ACTION_VERIFYING, (int)BurnPhase.Verifying);
    }

    [Fact]
    public void VerificationLevelsMatchTheBindings()
    {
        Assert.Equal((int)IMAPI_BURN_VERIFICATION_LEVEL.IMAPI_BURN_VERIFICATION_NONE, (int)BurnVerifyLevel.None);
        Assert.Equal((int)IMAPI_BURN_VERIFICATION_LEVEL.IMAPI_BURN_VERIFICATION_QUICK, (int)BurnVerifyLevel.Quick);
        Assert.Equal((int)IMAPI_BURN_VERIFICATION_LEVEL.IMAPI_BURN_VERIFICATION_FULL, (int)BurnVerifyLevel.Full);
    }

    [Fact]
    public void FileSystemFlagsMatchTheBindings()
    {
        Assert.Equal((int)FsiFileSystems.FsiFileSystemISO9660, (int)DiscFileSystems.Iso9660);
        Assert.Equal((int)FsiFileSystems.FsiFileSystemJoliet, (int)DiscFileSystems.Joliet);
        Assert.Equal((int)FsiFileSystems.FsiFileSystemUDF, (int)DiscFileSystems.Udf);
    }

    [Fact]
    public void BootEntryNumbersMatchTheBindings()
    {
        Assert.Equal((int)PlatformId.PlatformX86, (int)BootPlatform.Bios);
        Assert.Equal((int)PlatformId.PlatformEFI, (int)BootPlatform.Efi);
        Assert.Equal((int)EmulationType.EmulationNone, (int)BootEmulation.None);
        Assert.Equal((int)EmulationType.Emulation12MFloppy, (int)BootEmulation.Floppy1200K);
        Assert.Equal((int)EmulationType.Emulation144MFloppy, (int)BootEmulation.Floppy1440K);
        Assert.Equal((int)EmulationType.Emulation288MFloppy, (int)BootEmulation.Floppy2880K);
        Assert.Equal((int)EmulationType.EmulationHardDisk, (int)BootEmulation.HardDisk);
    }

    [Fact]
    public void UdfRevisionsAreTheHexNumbersImapiExpects()
    {
        Assert.Equal(0x102, (int)UdfRevision.Udf102);
        Assert.Equal(0x150, (int)UdfRevision.Udf150);
        Assert.Equal(0x200, (int)UdfRevision.Udf200);
        Assert.Equal(0x201, (int)UdfRevision.Udf201);
        Assert.Equal(0x250, (int)UdfRevision.Udf250);
    }

    [Fact]
    public void EventInterfacesHaveTheIdsImapiAnnounces()
    {
        // The IIDs used to hook the event sinks; IMAPI refuses a sink for any other interface.
        Assert.Equal(new Guid("2735413C-7F64-5B0F-8F00-5D77AFBE261E"), typeof(DDiscFormat2DataEvents).GUID);
        Assert.Equal(new Guid("2735413A-7F64-5B0F-8F00-5D77AFBE261E"), typeof(DDiscFormat2EraseEvents).GUID);
        Assert.Equal(new Guid("27354131-7F64-5B0F-8F00-5D77AFBE261E"), typeof(DDiscMaster2Events).GUID);
        Assert.Equal(0x200u, PInvoke.DISPID_DDISCFORMAT2DATAEVENTS_UPDATE);
    }

    [Fact]
    public void ClassIdsOfTheImapiObjectsAreThoseOfTheDocumentedCoClasses()
    {
        Assert.Equal(new Guid("2735412E-7F64-5B0F-8F00-5D77AFBE261E"), typeof(MsftDiscMaster2).GUID);
        Assert.Equal(new Guid("2735412D-7F64-5B0F-8F00-5D77AFBE261E"), typeof(MsftDiscRecorder2).GUID);
        Assert.Equal(new Guid("2735412A-7F64-5B0F-8F00-5D77AFBE261E"), typeof(MsftDiscFormat2Data).GUID);
        Assert.Equal(new Guid("2C941FC5-975B-59BE-A960-9A2A262853A5"), typeof(MsftFileSystemImage).GUID);
    }

    [Fact]
    public void ControlCodesMatchTheBindings()
    {
        Assert.Equal(PInvoke.IOCTL_CDROM_READ_TOC_EX, OpticalIoctl.ReadTocEx);
        Assert.Equal(PInvoke.IOCTL_CDROM_GET_DRIVE_GEOMETRY_EX, OpticalIoctl.GetDriveGeometryEx);
        Assert.Equal(PInvoke.IOCTL_CDROM_SET_SPEED, OpticalIoctl.SetSpeed);
        Assert.Equal(PInvoke.IOCTL_DVD_READ_STRUCTURE, OpticalIoctl.DvdReadStructure);
    }

    [Fact]
    public void TocFormatAndCopyrightFormatNumbersMatchTheBindings()
    {
        Assert.Equal(1u, (uint)DVD_STRUCTURE_FORMAT.DvdCopyrightDescriptor);
        Assert.Equal(OpticalIoctl.DvdCopyrightDescriptor, (uint)DVD_STRUCTURE_FORMAT.DvdCopyrightDescriptor);
    }

    [Fact]
    public void DvdReadStructureRequestIsPacked()
    {
        // The driver checks the input length; 8 + 4 + 4 + 1 bytes without padding.
        unsafe
        {
            Assert.Equal(17, sizeof(DVD_READ_STRUCTURE));
        }
    }
}
