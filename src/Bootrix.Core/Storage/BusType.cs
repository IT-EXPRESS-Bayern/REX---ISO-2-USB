// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

/// <summary>Subset of STORAGE_BUS_TYPE; numeric values match the Windows enumeration.</summary>
public enum BusType
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    RAID = 8,
    IScsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
    Scm = 18,
    Ufs = 19,
}

public enum DiskPartitionStyle
{
    Raw,
    Mbr,
    Gpt,
}
