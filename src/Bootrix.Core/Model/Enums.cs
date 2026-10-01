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
