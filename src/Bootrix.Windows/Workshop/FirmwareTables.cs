// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Bootrix.Windows.Workshop;

/// <summary>GetSystemFirmwareTable: the ACPI tables and the SMBIOS structure table as raw bytes.</summary>
internal static unsafe class FirmwareTables
{
    /// <summary>
    /// The provider signature is the four characters read as a big-endian number ('ACPI' = 0x41435049), but the table ID is
    /// the table's signature as it lies in memory, which is little-endian: 'MSDM' becomes 0x4D44534D.
    /// </summary>
    public static readonly uint AcpiProvider = BinaryPrimitives.ReadUInt32BigEndian("ACPI"u8);

    public static readonly uint SmbiosProvider = BinaryPrimitives.ReadUInt32BigEndian("RSMB"u8);

    public static uint AcpiTableId(string signature) => BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(signature));

    /// <summary>Returns null when the firmware has no such table.</summary>
    public static byte[]? Read(uint provider, uint tableId)
    {
        var size = FirmwareNative.GetSystemFirmwareTable(provider, tableId, null, 0);
        if (size == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is FirmwareNative.ErrorNotFound or FirmwareNative.ErrorInvalidFunction or FirmwareNative.ErrorInvalidParameter)
            {
                return null;
            }

            throw new Win32Exception(error);
        }

        var buffer = new byte[size];
        fixed (byte* pointer = buffer)
        {
            var written = FirmwareNative.GetSystemFirmwareTable(provider, tableId, pointer, size);
            if (written == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return written == size ? buffer : buffer[..(int)Math.Min(written, size)];
        }
    }
}
