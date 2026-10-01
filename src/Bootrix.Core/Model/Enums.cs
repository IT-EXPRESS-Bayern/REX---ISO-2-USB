// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Model;

public enum JobKind
{
    WriteImage,
    Burn,
    Rip,
    Download,
    TinyBuild,
    Backup,
    Restore,
    Erase,
    Verify,
}

public enum PartitionScheme
{
    Auto,
    Mbr,
    Gpt,
}

public enum TargetFirmware
{
    Auto,
    Bios,
    Uefi,
    BiosAndUefi,
}

public enum FileSystemKind
{
    Auto,
    Fat12,
    Fat16,
    Fat32,
    ExFat,
    Ntfs,
    Udf,
    ReFs,
    Ext3,
}

public enum WriteMode
{
    Auto,
    /// <summary>Copy the image sector by sector (hybrid ISOs, raw disk images).</summary>
    RawCopy,
    /// <summary>Extract the files of an ISO onto a freshly formatted partition.</summary>
    Extract,
}

public enum BootCertificate
{
    Auto,
    Windows2011,
    Windows2023,
}

/// <summary>What to do when the Windows image already carries an autounattend.xml and Bootrix has one of its own to write.</summary>
public enum ExistingAnswerFilePolicy
{
    /// <summary>Write Bootrix' file and keep the old one next to it as autounattend.xml.original.</summary>
    ReplaceAndKeepOriginal,

    /// <summary>Write Bootrix' file; the old one is lost.</summary>
    Replace,

    /// <summary>Leave the image's file alone and write nothing.</summary>
    Keep,

    /// <summary>Stop the job.</summary>
    Fail,
}
