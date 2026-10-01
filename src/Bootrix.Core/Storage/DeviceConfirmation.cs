// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

public static class DeviceConfirmation
{
    /// <summary>
    /// The text a user types to confirm a destructive write: the serial number, or "disk&lt;N&gt;" when the
    /// device has none. Typing something device-specific stops the habit of just clicking "OK".
    /// </summary>
    public static string TextFor(StorageDevice device) =>
        string.IsNullOrWhiteSpace(device.Serial) ? $"disk{device.DiskNumber}" : device.Serial.Trim();

    public static bool Matches(StorageDevice device, string? typed) =>
        string.Equals(TextFor(device), typed?.Trim(), StringComparison.OrdinalIgnoreCase);
}
