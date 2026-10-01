// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;

namespace Bootrix.Core.Workshop;

public enum DiskMediaType
{
    Unknown,
    Hdd,
    Ssd,
    StorageClassMemory,
}

public sealed record DiskInfo
{
    public int Number { get; init; }

    public string Name { get; init; } = "";

    public long SizeBytes { get; init; }

    public BusType Bus { get; init; }

    public DiskMediaType MediaType { get; init; }

    public DiskPartitionStyle PartitionStyle { get; init; }

    public bool IsSystemDisk { get; init; }

    public bool IsRemovable { get; init; }

    /// <summary>A disk Windows could be installed on: not a stick, a card or a mounted image file.</summary>
    public bool IsInternal => !IsRemovable && Bus is not (BusType.Usb or BusType.Ieee1394 or BusType.FileBackedVirtual);

    public static DiskInfo From(StorageDevice device, DiskMediaType mediaType = DiskMediaType.Unknown) => new()
    {
        Number = device.DiskNumber,
        Name = device.DisplayName,
        SizeBytes = device.SizeBytes,
        Bus = device.Bus,
        MediaType = mediaType,
        PartitionStyle = device.PartitionStyle,
        IsSystemDisk = (device.Protection & (DeviceProtection.SystemDisk | DeviceProtection.BootDisk)) != 0,
        IsRemovable = device.IsRemovableMedia,
    };
}
