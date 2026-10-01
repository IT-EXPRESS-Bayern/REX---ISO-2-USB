// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Storage;

internal sealed record DriveLayout(DiskPartitionStyle Style, string? Signature, IReadOnlyList<ExistingPartition> Partitions);

/// <summary>Parses DRIVE_LAYOUT_INFORMATION_EX by offset; the structure holds a union and a variable-length array.</summary>
internal static class DriveLayoutReader
{
    private const int HeaderSize = 48;
    private const int EntrySize = 144;

    private static readonly Guid EfiSystem = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    private static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    private static readonly Guid LogicalDiskManagerData = new("af9b60a0-1431-4f62-bc68-3311714a69ad");
    private static readonly Guid LogicalDiskManagerMetadata = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");
    private static readonly Guid StorageSpaces = new("e75caf8f-f680-4cee-afa3-b001e56efc2d");

    public static DriveLayout Read(SafeFileHandle handle)
    {
        var data = DeviceIo.QueryGrowing(handle, Ioctl.DiskGetDriveLayoutEx, [], 4096);
        return data is null ? new DriveLayout(DiskPartitionStyle.Raw, null, []) : Parse(data);
    }

    public static DriveLayout Parse(byte[] data)
    {
        if (data.Length < HeaderSize)
        {
            return new DriveLayout(DiskPartitionStyle.Raw, null, []);
        }

        var style = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var partitionStyle = style switch
        {
            0 => DiskPartitionStyle.Mbr,
            1 => DiskPartitionStyle.Gpt,
            _ => DiskPartitionStyle.Raw,
        };

        var signature = partitionStyle switch
        {
            DiskPartitionStyle.Mbr => $"{BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)):X8}",
            DiskPartitionStyle.Gpt => new Guid(data.AsSpan(8, 16)).ToString("D"),
            _ => null,
        };

        var partitions = new List<ExistingPartition>();
        for (var i = 0; i < count && HeaderSize + (i + 1) * EntrySize <= data.Length; i++)
        {
            var entry = data.AsSpan(HeaderSize + i * EntrySize, EntrySize);
            var partition = ParseEntry(entry);
            if (partition is not null)
            {
                partitions.Add(partition);
            }
        }

        return new DriveLayout(partitionStyle, signature, partitions);
    }

    private static ExistingPartition? ParseEntry(ReadOnlySpan<byte> entry)
    {
        var style = BinaryPrimitives.ReadUInt32LittleEndian(entry);
        var offset = BinaryPrimitives.ReadInt64LittleEndian(entry[8..]);
        var length = BinaryPrimitives.ReadInt64LittleEndian(entry[16..]);
        var number = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]);

        if (style == 0)
        {
            var type = entry[32];
            if (type == 0 || length == 0)
            {
                return null;
            }

            return new ExistingPartition
            {
                Number = number,
                Offset = offset,
                Length = length,
                Type = $"0x{type:X2}",
                IsEfiSystem = type == 0xEF,
                IsManagedByOtherStack = type == 0x42,
            };
        }

        if (style != 1)
        {
            return null;
        }

        var typeGuid = new Guid(entry.Slice(32, 16));
        if (typeGuid == Guid.Empty)
        {
            return null;
        }

        var name = Encoding.Unicode.GetString(entry.Slice(72, 72));
        var end = name.IndexOf('\0', StringComparison.Ordinal);
        return new ExistingPartition
        {
            Number = number,
            Offset = offset,
            Length = length,
            Type = typeGuid.ToString("D"),
            Name = (end >= 0 ? name[..end] : name).Trim() is { Length: > 0 } text ? text : null,
            IsEfiSystem = typeGuid == EfiSystem,
            IsMicrosoftReserved = typeGuid == MicrosoftReserved,
            IsManagedByOtherStack = typeGuid == LogicalDiskManagerData
                || typeGuid == LogicalDiskManagerMetadata
                || typeGuid == StorageSpaces,
        };
    }

    public static bool IsStorageSpaces(ExistingPartition partition) =>
        string.Equals(partition.Type, StorageSpaces.ToString("D"), StringComparison.OrdinalIgnoreCase);
}
