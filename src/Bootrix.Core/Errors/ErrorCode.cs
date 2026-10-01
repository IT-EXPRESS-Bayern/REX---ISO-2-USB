// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Errors;

/// <summary>
/// Stable error codes. The numeric value is shown to users and used in support requests,
/// so values must never be reused or renumbered.
/// </summary>
public enum ErrorCode
{
    Unknown = 0,

    // 1xxx: general / job handling
    Canceled = 1001,
    InvalidSpec = 1002,
    UnsupportedSchemaVersion = 1003,
    ProfileLocked = 1004,
    ProfileCycle = 1005,
    ProfilePackageCorrupt = 1006,
    JournalCorrupt = 1007,

    // 2xxx: devices and disks
    DeviceNotFound = 2001,
    DeviceChanged = 2002,
    DeviceProtected = 2003,
    DeviceBusy = 2004,
    DeviceTooSmall = 2005,
    DeviceWriteProtected = 2006,
    DeviceRemoved = 2007,
    LayoutRejected = 2008,
    VolumeNotMounted = 2009,
    SectorSizeUnsupported = 2010,
    FileSystemUnsupported = 2011,
    FileTooLargeForFileSystem = 2012,
    FileSystemTooSmall = 2013,
    WriteModeUnsupported = 2014,
    PersistenceTooSmall = 2015,
    MediaWriterUnavailable = 2016,

    // 204x: extras of the raw writer
    PersistenceLayoutUnsupported = 2041,

    // 3xxx: images
    ImageUnreadable = 3001,
    ImageTruncated = 3002,
    ImageUnsupported = 3003,
    ImageEncrypted = 3004,
    ImageHashMismatch = 3005,
    ImageTooLarge = 3006,

    // 4xxx: verification
    VerifyMismatch = 4001,
    VerifyFilesDiffer = 4041,

    // 5xxx: network and catalog
    DownloadFailed = 5001,
    DownloadBlocked = 5002,
    DownloadHashMismatch = 5003,
    CatalogUnavailable = 5004,
    SignatureInvalid = 5005,
    ChecksumFileInvalid = 5006,
    MetalinkInvalid = 5007,

    // 6xxx: Windows servicing and tools
    ExternalToolFailed = 6001,
    ExternalToolUntrusted = 6002,
    InsufficientSpace = 6003,
    ImageMountFailed = 6004,

    // 61xx: changes to Windows setup media after copying
    AnswerFileExists = 6101,
    DriverFolderRejected = 6102,
    DriverInjectionFailed = 6103,
    BootManager2023Unavailable = 6104,
    BootManager2023Unverified = 6105,

    // 62xx: Linux and other file-copy media
    BootloaderInstallFailed = 6201,
    FileCopyFailed = 6202,

    // 63xx: Windows setup media (copying, boot code, read-back)
    WimLibraryMissing = 6301,
    BootCodeUnavailable = 6302,
    MediaFileMismatch = 6303,
    SplitSetInvalid = 6304,

    // 7xxx: optical
    NoRecorder = 7001,
    MediaNotSupported = 7002,
    BurnFailed = 7003,
    ReadError = 7004,
    CopyProtected = 7005,
    MediaNotBlank = 7006,
    AudioDiscNotSupported = 7007,
    MixedModeDiscNotSupported = 7008,
    DiscFileTooLarge = 7009,
    OpticalUnavailable = 7010,

    // 8xxx: Secure Boot / EFI analysis
    EfiBinaryInvalid = 8001,
    RevocationDataInvalid = 8002,

    // 31xx: Apple disk images
    ImageCorrupt = 3101,
    ImageSegmented = 3102,
    ImageLegacyFormat = 3103,
    ImageAppleArchive = 3104,
    BlockMapInvalid = 3141,

    // 81xx: elevated broker process
    ElevationDenied = 8101,
    BrokerStartFailed = 8102,
    BrokerDisconnected = 8103,
    BrokerProtocol = 8104,

    // DOS and diskette media
    DosMbrNotBootable = 2081,
    MsDosImageInvalid = 3081,
    MsDosNotDownloaded = 5081,
    MsDosFilesUntrusted = 5082,
}
