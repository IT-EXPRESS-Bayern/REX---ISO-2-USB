// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Windows.Storage;

/// <summary>Builds the byte images of CREATE_DISK and DRIVE_LAYOUT_INFORMATION_EX (offsets as in winioctl.h, identical on x86, x64 and ARM64).</summary>
internal static class DriveLayoutBuilder
{
    public const int HeaderSize = 48;
    public const int EntrySize = 144;
    public const int CreateDiskSize = 24;

    private const int GptEntryArrayBytes = 128 * 128;
    private const int MaxGptNameChars = 36;

    public static byte[] BuildCreateDisk(LayoutSpec spec)
    {
        var data = new byte[CreateDiskSize];
        if (spec.Style == LayoutStyle.Mbr)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), spec.MbrSignature);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(data, 1);
            spec.GptDiskId.TryWriteBytes(data.AsSpan(4));
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 128);
        }

        return data;
    }

    /// <summary>A RAW CREATE_DISK, used first to reset whatever table was there before.</summary>
    public static byte[] BuildCreateRawDisk()
    {
        var data = new byte[CreateDiskSize];
        BinaryPrimitives.WriteInt32LittleEndian(data, 2);
        return data;
    }

    public static byte[] BuildLayout(LayoutSpec spec)
    {
        // An MBR holds exactly four slots, and Windows wants all of them described.
        var count = spec.Style == LayoutStyle.Mbr ? 4 : spec.Partitions.Count;
        if (spec.Partitions.Count > count)
        {
            throw new ArgumentException("An MBR cannot hold more than four partitions.", nameof(spec));
        }

        var data = new byte[HeaderSize + EntrySize * count];
        BinaryPrimitives.WriteUInt32LittleEndian(data, spec.Style == LayoutStyle.Mbr ? 0u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)count);

        if (spec.Style == LayoutStyle.Mbr)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), spec.MbrSignature);
        }
        else
        {
            WriteGptHeader(data, spec);
        }

        for (var i = 0; i < count; i++)
        {
            var entry = data.AsSpan(HeaderSize + i * EntrySize, EntrySize);
            if (i < spec.Partitions.Count)
            {
                WriteEntry(entry, spec, spec.Partitions[i], i + 1);
            }
            else
            {
                // Unused MBR slot: rewrite it as empty so an old entry cannot survive.
                BinaryPrimitives.WriteUInt32LittleEndian(entry, 0);
                entry[28] = 1;
            }
        }

        return data;
    }

    internal static (long FirstUsable, long UsableLength) GptUsableRange(long diskSize, int sectorSize)
    {
        var first = RoundUp(2L * sectorSize + GptEntryArrayBytes, sectorSize);
        var tail = GptEntryArrayBytes + sectorSize;
        return (first, diskSize - first - tail);
    }

    private static void WriteGptHeader(byte[] data, LayoutSpec spec)
    {
        spec.GptDiskId.TryWriteBytes(data.AsSpan(8));
        var (first, length) = GptUsableRange(spec.DiskSizeBytes, spec.SectorSize);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24), first);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 128);
    }

    private static void WriteEntry(Span<byte> entry, LayoutSpec spec, PartitionSpec partition, int number)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(entry, spec.Style == LayoutStyle.Mbr ? 0u : 1u);
        BinaryPrimitives.WriteInt64LittleEndian(entry[8..], partition.OffsetBytes);
        BinaryPrimitives.WriteInt64LittleEndian(entry[16..], partition.LengthBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], (uint)number);
        entry[28] = 1; // RewritePartition: must be TRUE or Windows keeps the old entry

        if (spec.Style == LayoutStyle.Mbr)
        {
            entry[32] = partition.MbrType;
            entry[33] = partition.Active ? (byte)1 : (byte)0;
            entry[34] = 1; // RecognizedPartition
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], (uint)(partition.OffsetBytes / spec.SectorSize));
            return;
        }

        partition.GptType.TryWriteBytes(entry[32..]);
        (partition.GptId ?? Guid.NewGuid()).TryWriteBytes(entry[48..]);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[64..], partition.GptAttributes);

        var name = partition.Name.Length > MaxGptNameChars ? partition.Name[..MaxGptNameChars] : partition.Name;
        Encoding.Unicode.GetBytes(name, entry.Slice(72, MaxGptNameChars * 2));
    }

    private static long RoundUp(long value, int multiple) => (value + multiple - 1) / multiple * multiple;
}
