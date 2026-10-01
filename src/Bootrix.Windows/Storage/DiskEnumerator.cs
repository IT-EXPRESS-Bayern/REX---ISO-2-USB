// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Storage;

public sealed class DiskEnumerator(ILogger<DiskEnumerator>? logger = null) : IDiskService, IDisposable
{
    private readonly ILogger _log = logger ?? NullLogger<DiskEnumerator>.Instance;
    private DiskWatcher? _watcher;

    public event EventHandler? DevicesChanged
    {
        add
        {
            _watcher ??= new DiskWatcher();
            _watcher.Changed += value;
        }

        remove
        {
            if (_watcher is not null)
            {
                _watcher.Changed -= value;
            }
        }
    }

    public IReadOnlyList<StorageDevice> Enumerate(DiskFilter filter)
    {
        using var errorMode = new ErrorModeScope();
        var volumes = VolumeCatalog.Load();
        var protection = SystemDiskDetector.Detect(volumes);

        var devices = new List<StorageDevice>();
        foreach (var path in SetupApi.EnumerateInterfacePaths(SetupApi.DiskInterfaceGuid))
        {
            try
            {
                var device = Describe(path, volumes, protection);
                if (device is not null && IsListed(device, filter))
                {
                    devices.Add(device);
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                _log.LogWarning(ex, "Skipping {Path}", path);
            }
        }

        return [.. devices.OrderBy(d => d.DiskNumber)];
    }

    public StorageDevice? Find(string devicePath)
    {
        using var errorMode = new ErrorModeScope();
        var volumes = VolumeCatalog.Load();
        return Describe(devicePath, volumes, SystemDiskDetector.Detect(volumes));
    }

    public void Dispose() => _watcher?.Dispose();

    private static bool IsListed(StorageDevice device, DiskFilter filter)
    {
        if (device.IsBlocked && !filter.IncludeBlocked)
        {
            return false;
        }

        if (!device.HasMedia && !filter.IncludeEmptyReaders)
        {
            return false;
        }

        if ((device.Protection & DeviceProtection.Virtual) != 0)
        {
            return filter.IncludeVirtualDisks;
        }

        var removableBus = device.Bus is BusType.Usb or BusType.Sd or BusType.Mmc or BusType.Ieee1394;
        if (device.IsRemovableMedia && removableBus)
        {
            return true;
        }

        if (device.Bus is BusType.Usb or BusType.Ieee1394)
        {
            return filter.IncludeUsbHardDisks || filter.IncludeInternalDisks;
        }

        return filter.IncludeInternalDisks;
    }

    private static StorageDevice? Describe(string path, List<VolumeEntry> volumes, Dictionary<int, DeviceProtection> systemDisks)
    {
        using var handle = DeviceIo.OpenForQuery(path);
        if (handle is null)
        {
            return null;
        }

        var number = StorageQueries.GetDeviceNumber(handle);
        if (number is not { DeviceType: StorageQueries.DeviceTypeDisk })
        {
            return null;
        }

        var diskNumber = (int)number.Value.Number;
        var descriptor = StorageQueries.GetDeviceDescriptor(handle);
        var bus = descriptor?.Bus ?? BusType.Unknown;

        var hasMedia = StorageQueries.HasMedia(handle);
        var size = hasMedia ? StorageQueries.GetLength(handle) ?? StorageQueries.GetGeometry(handle)?.Size ?? 0 : 0;
        var geometry = hasMedia ? StorageQueries.GetGeometry(handle) : null;
        var sectors = hasMedia ? StorageQueries.GetSectorSizes(handle) : null;
        var logical = sectors?.Logical ?? geometry?.BytesPerSector ?? 512;
        var physical = sectors?.Physical ?? logical;

        var layout = hasMedia ? DriveLayoutReader.Read(handle) : new DriveLayout(DiskPartitionStyle.Raw, null, []);
        var diskVolumes = volumes
            .Where(v => v.Info.Extents.Any(e => e.DiskNumber == diskNumber))
            .Select(v => v.Info)
            .ToList();

        var protection = systemDisks.GetValueOrDefault(diskNumber);
        if (layout.Partitions.Any(p => p.IsManagedByOtherStack))
        {
            protection |= layout.Partitions.Any(DriveLayoutReader.IsStorageSpaces)
                ? DeviceProtection.StorageSpaces
                : DeviceProtection.DynamicDisk;
        }

        if (bus is BusType.Virtual or BusType.FileBackedVirtual)
        {
            protection |= DeviceProtection.Virtual;
        }

        var removableBus = bus is BusType.Usb or BusType.Sd or BusType.Mmc or BusType.Ieee1394;
        if (!(descriptor?.RemovableMedia ?? false) && !removableBus && (protection & DeviceProtection.Virtual) == 0)
        {
            protection |= DeviceProtection.InternalFixedDisk;
        }

        if (!hasMedia)
        {
            protection |= DeviceProtection.NoMedia;
        }

        var writable = hasMedia && StorageQueries.IsWritable(handle);
        if (hasMedia && !writable)
        {
            protection |= DeviceProtection.WriteProtected;
        }

        return new StorageDevice
        {
            DiskNumber = diskNumber,
            DevicePath = path,
            Vendor = descriptor?.Vendor ?? "",
            Product = descriptor?.Product ?? "",
            Revision = descriptor?.Revision ?? "",
            Serial = descriptor?.Serial,
            DeviceGuid = StorageQueries.GetDeviceGuid(handle)?.ToString("D"),
            Bus = bus,
            SizeBytes = size,
            LogicalSectorSize = logical,
            PhysicalSectorSize = physical,
            IsRemovableMedia = descriptor?.RemovableMedia ?? false,
            HasMedia = hasMedia,
            IsWritable = writable || !hasMedia,
            PartitionStyle = layout.Style,
            PartitionSignature = layout.Signature,
            Partitions = layout.Partitions,
            Volumes = diskVolumes,
            Protection = protection,
        };
    }
}
