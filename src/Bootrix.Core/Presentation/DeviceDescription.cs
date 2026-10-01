// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Localization;
using Bootrix.Core.Storage;
using Bootrix.Core.Text;

namespace Bootrix.Core.Presentation;

/// <summary>How a disk appears in a list: what it is, why it cannot be chosen, what deserves a second look.</summary>
public sealed record DeviceDescription(
    string Title,
    string Details,
    string SizeText,
    string? BlockReason,
    string? Warning,
    string ConfirmationText)
{
    public bool Selectable => BlockReason is null;

    // Hard blocks come first so the most important reason is the one shown when several apply.
    private static readonly DeviceProtection[] BlockingOrder =
    [
        DeviceProtection.SystemDisk,
        DeviceProtection.BootDisk,
        DeviceProtection.PagefileDisk,
        DeviceProtection.DynamicDisk,
        DeviceProtection.StorageSpaces,
        DeviceProtection.NoMedia,
        DeviceProtection.WriteProtected,
        DeviceProtection.Offline,
    ];

    public static DeviceDescription Describe(StorageDevice device, Localizer localizer, CultureInfo? culture = null)
    {
        culture ??= localizer.Culture;
        var details = new List<string> { BusName(device.Bus) };

        if (device.DriveLetters.Length > 0)
        {
            details.Add(localizer.Get("Device.Letters", device.DriveLetters));
        }

        details.Add(localizer.Get("Device.Partitions", device.Partitions.Count));
        if (!string.IsNullOrWhiteSpace(device.Serial))
        {
            details.Add(localizer.Get("Device.Serial", device.Serial.Trim()));
        }

        return new DeviceDescription(
            $"{device.DisplayName}",
            string.Join("  ·  ", details),
            ByteSize.Format(device.SizeBytes, culture),
            BlockReasonFor(device, localizer),
            WarningFor(device, localizer),
            DeviceConfirmation.TextFor(device));
    }

    private static string? BlockReasonFor(StorageDevice device, Localizer localizer)
    {
        var reasons = BlockingOrder
            .Where(flag => device.Protection.HasFlag(flag))
            .Select(flag => localizer.Get("Protect." + flag))
            .ToList();

        if (!device.HasMedia && !device.Protection.HasFlag(DeviceProtection.NoMedia))
        {
            reasons.Add(localizer.Get("Protect.NoMedia"));
        }

        if (!device.IsWritable && !device.Protection.HasFlag(DeviceProtection.WriteProtected))
        {
            reasons.Add(localizer.Get("Protect.WriteProtected"));
        }

        return reasons.Count == 0 ? null : string.Join(", ", reasons);
    }

    private static string? WarningFor(StorageDevice device, Localizer localizer)
    {
        var warnings = new List<string>();
        if (device.Protection.HasFlag(DeviceProtection.InternalFixedDisk))
        {
            warnings.Add(localizer.Get("Protect.InternalFixedDisk"));
        }

        if (device.Protection.HasFlag(DeviceProtection.Virtual))
        {
            warnings.Add(localizer.Get("Protect.Virtual"));
        }

        return warnings.Count == 0 ? null : string.Join(", ", warnings);
    }

    private static string BusName(BusType bus) => bus switch
    {
        BusType.Usb => "USB",
        BusType.Sd => "SD",
        BusType.Mmc => "MMC",
        BusType.Sata => "SATA",
        BusType.Ata => "ATA",
        BusType.Nvme => "NVMe",
        BusType.Scsi => "SCSI",
        BusType.Sas => "SAS",
        BusType.Ieee1394 => "FireWire",
        BusType.Atapi => "ATAPI",
        BusType.Virtual or BusType.FileBackedVirtual => "VHD",
        BusType.Unknown => "?",
        _ => bus.ToString(),
    };
}
