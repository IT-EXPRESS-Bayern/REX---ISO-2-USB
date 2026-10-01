// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Interop;

internal sealed record DeviceDescriptor(
    string Vendor,
    string Product,
    string Revision,
    string? Serial,
    bool RemovableMedia,
    BusType Bus);

internal readonly record struct DeviceNumber(uint DeviceType, uint Number, int Partition);

/// <summary>Reads the variable-length storage structures. They are parsed by offset, not marshalled.</summary>
internal static class StorageQueries
{
    public const int StorageDeviceProperty = 0;
    public const int StorageAccessAlignmentProperty = 6;
    public const uint DeviceTypeDisk = 7;

    public static DeviceNumber? GetDeviceNumber(SafeFileHandle handle)
    {
        Span<byte> buffer = stackalloc byte[12];
        if (!DeviceIo.TryControl(handle, Ioctl.StorageGetDeviceNumber, [], buffer, out var returned, out _) || returned < 12)
        {
            return null;
        }

        return new DeviceNumber(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(buffer[8..]));
    }

    /// <summary>STORAGE_DEVICE_NUMBER_EX: the device GUID survives a re-enumeration, the disk number does not.</summary>
    public static Guid? GetDeviceGuid(SafeFileHandle handle)
    {
        Span<byte> buffer = stackalloc byte[40];
        if (!DeviceIo.TryControl(handle, Ioctl.StorageGetDeviceNumberEx, [], buffer, out var returned, out _) || returned < 40)
        {
            return null;
        }

        var guid = new Guid(buffer.Slice(20, 16));
        return guid == Guid.Empty ? null : guid;
    }

    public static DeviceDescriptor? GetDeviceDescriptor(SafeFileHandle handle) =>
        ParseDeviceDescriptor(QueryProperty(handle, StorageDeviceProperty));

    internal static DeviceDescriptor? ParseDeviceDescriptor(byte[]? data)
    {
        if (data is null || data.Length < 36)
        {
            return null;
        }

        return new DeviceDescriptor(
            ReadAscii(data, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12))),
            ReadAscii(data, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16))),
            ReadAscii(data, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20))),
            NullIfEmpty(ReadAscii(data, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(24)))),
            data[10] != 0,
            (BusType)BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(28)));
    }

    /// <summary>Logical and physical sector size; many USB bridges refuse this query, so null is a normal answer.</summary>
    public static (int Logical, int Physical)? GetSectorSizes(SafeFileHandle handle)
    {
        var data = QueryProperty(handle, StorageAccessAlignmentProperty);
        if (data is null || data.Length < 28)
        {
            return null;
        }

        var logical = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(16));
        var physical = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(20));
        return logical > 0 && physical > 0 ? (logical, physical) : null;
    }

    public static (long Size, int BytesPerSector)? GetGeometry(SafeFileHandle handle)
    {
        Span<byte> buffer = stackalloc byte[128];
        if (!DeviceIo.TryControl(handle, Ioctl.DiskGetDriveGeometryEx, [], buffer, out var returned, out _) || returned < 32)
        {
            return null;
        }

        var bytesPerSector = BinaryPrimitives.ReadInt32LittleEndian(buffer[20..]);
        var size = BinaryPrimitives.ReadInt64LittleEndian(buffer[24..]);
        return (size, bytesPerSector);
    }

    public static long? GetLength(SafeFileHandle handle)
    {
        Span<byte> buffer = stackalloc byte[8];
        return DeviceIo.TryControl(handle, Ioctl.DiskGetLengthInfo, [], buffer, out var returned, out _) && returned == 8
            ? BinaryPrimitives.ReadInt64LittleEndian(buffer)
            : null;
    }

    public static bool HasMedia(SafeFileHandle handle) =>
        DeviceIo.TryControl(handle, Ioctl.StorageCheckVerify2, [], [], out _, out _);

    public static bool IsWritable(SafeFileHandle handle) =>
        DeviceIo.TryControl(handle, Ioctl.DiskIsWritable, [], [], out _, out var error) || error != Kernel32.ErrorWriteProtect;

    /// <summary>Two-step query: the header tells how large the real descriptor is.</summary>
    private static byte[]? QueryProperty(SafeFileHandle handle, int propertyId)
    {
        Span<byte> query = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
        BinaryPrimitives.WriteInt32LittleEndian(query[4..], 0);

        Span<byte> header = stackalloc byte[8];
        if (!DeviceIo.TryControl(handle, Ioctl.StorageQueryProperty, query, header, out _, out _))
        {
            return null;
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (size is < 8 or > 64 * 1024)
        {
            return null;
        }

        var buffer = new byte[size];
        return DeviceIo.TryControl(handle, Ioctl.StorageQueryProperty, query, buffer, out _, out _) ? buffer : null;
    }

    private static string ReadAscii(byte[] data, uint offset)
    {
        if (offset == 0 || offset >= data.Length)
        {
            return "";
        }

        var end = Array.IndexOf(data, (byte)0, (int)offset);
        if (end < 0)
        {
            end = data.Length;
        }

        return Encoding.ASCII.GetString(data, (int)offset, end - (int)offset).Trim();
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
