// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

public readonly record struct DiskExtent(int DiskNumber, long StartingOffset, long Length);

public sealed record VolumeInfo
{
    /// <summary>\\?\Volume{guid}\ path, always with a trailing backslash.</summary>
    public required string VolumeGuidPath { get; init; }

    public IReadOnlyList<string> MountPoints { get; init; } = [];

    public string? Label { get; init; }

    public string? FileSystem { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    public IReadOnlyList<DiskExtent> Extents { get; init; } = [];

    /// <summary>The volume exists but its file system could not be read (locked by BitLocker, damaged or RAW).</summary>
    public bool Unreadable { get; init; }

    public string? DriveLetter => MountPoints.FirstOrDefault(m => m.Length == 3 && m[1] == ':');
}

public sealed record ExistingPartition
{
    public int Number { get; init; }

    public long Offset { get; init; }

    public long Length { get; init; }

    /// <summary>Partition type as text: a GPT type GUID or an MBR type byte like "0x07".</summary>
    public string Type { get; init; } = "";

    public string? Name { get; init; }

    public bool IsEfiSystem { get; init; }

    public bool IsMicrosoftReserved { get; init; }

    /// <summary>Dynamic disk (LDM) or Storage Spaces member: cannot be repartitioned safely from here.</summary>
    public bool IsManagedByOtherStack { get; init; }
}

[Flags]
public enum DeviceProtection
{
    None = 0,
    SystemDisk = 1 << 0,
    BootDisk = 1 << 1,
    PagefileDisk = 1 << 2,
    DynamicDisk = 1 << 3,
    StorageSpaces = 1 << 4,
    InternalFixedDisk = 1 << 5,
    Virtual = 1 << 6,
    NoMedia = 1 << 7,
    WriteProtected = 1 << 8,
    Offline = 1 << 9,

    /// <summary>Reasons that can never be overridden, not even in service mode.</summary>
    HardBlock = SystemDisk | BootDisk | PagefileDisk | DynamicDisk | StorageSpaces,
}

public sealed record StorageDevice
{
    public required int DiskNumber { get; init; }

    /// <summary>Device interface path (\\?\...). Opening this instead of \\.\PhysicalDriveN ties the handle to the device, not to a number that can be reassigned.</summary>
    public required string DevicePath { get; init; }

    public string Vendor { get; init; } = "";

    public string Product { get; init; } = "";

    public string Revision { get; init; } = "";

    public string? Serial { get; init; }

    public string? DeviceGuid { get; init; }

    public BusType Bus { get; init; }

    public long SizeBytes { get; init; }

    public int LogicalSectorSize { get; init; } = 512;

    public int PhysicalSectorSize { get; init; } = 512;

    public bool IsRemovableMedia { get; init; }

    public bool HasMedia { get; init; } = true;

    public bool IsWritable { get; init; } = true;

    public bool IsFloppy { get; init; }

    public DiskPartitionStyle PartitionStyle { get; init; }

    public string? PartitionSignature { get; init; }

    public IReadOnlyList<ExistingPartition> Partitions { get; init; } = [];

    public IReadOnlyList<VolumeInfo> Volumes { get; init; } = [];

    public DeviceProtection Protection { get; init; }

    public string DisplayName
    {
        get
        {
            var name = $"{Vendor} {Product}".Trim();
            return name.Length > 0 ? name : $"Disk {DiskNumber}";
        }
    }

    public bool IsBlocked => (Protection & DeviceProtection.HardBlock) != 0;

    public bool Is4Kn => LogicalSectorSize >= 4096;

    /// <summary>Drive letters of all mounted volumes on this disk, e.g. "E:, F:".</summary>
    public string DriveLetters =>
        string.Join(", ", Volumes.Select(v => v.DriveLetter).Where(l => l is not null).Select(l => l![..2]));
}
