// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Bootrix.Core.Images.Disk;

/// <summary>Reads the MBR, GPT and Apple Partition Map signatures at the start of an image.</summary>
public static class DiskLayoutReader
{
    private const int MbrTableOffset = 446;
    private const int MaxGptEntries = 1024;

    private static ReadOnlySpan<byte> GptSignature => "EFI PART"u8;

    public static DiskLayout Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var first = new byte[512];
        if (ReadAt(stream, 0, first) < first.Length)
        {
            return new DiskLayout();
        }

        var hasSignature = first[510] == 0x55 && first[511] == 0xAA;
        var volumeImage = FatBootSector.LooksLikeFatBootSector(first);
        var partitions = hasSignature && !volumeImage ? ParseMbrTable(first) : [];
        var layout = new DiskLayout
        {
            HasMbrSignature = hasSignature,
            HasBootCode = first.AsSpan(0, 440).IndexOfAnyExcept((byte)0) >= 0,
            MbrPartitions = partitions,
            IsVolumeImage = volumeImage,
            HasApm = first[0] == (byte)'E' && first[1] == (byte)'R',
        };

        if (volumeImage)
        {
            return layout;
        }

        foreach (var sectorSize in new[] { 512, 4096 })
        {
            var gpt = TryReadGpt(stream, sectorSize);
            if (gpt is not null)
            {
                return layout with
                {
                    HasGpt = true,
                    GptHeaderValid = gpt.HeaderValid,
                    GptSectorSize = sectorSize,
                    GptBackupSector = gpt.BackupSector,
                    GptLastUsableSector = gpt.LastUsable,
                    GptDiskId = gpt.DiskId,
                    GptPartitions = gpt.Partitions,
                };
            }
        }

        return layout;
    }

    private static List<MbrPartition> ParseMbrTable(ReadOnlySpan<byte> sector)
    {
        var result = new List<MbrPartition>();
        for (var slot = 0; slot < 4; slot++)
        {
            var entry = sector.Slice(MbrTableOffset + (slot * 16), 16);
            var type = entry[4];
            var count = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            // xorriso marks the ISO's own partition with type 0, so a size alone makes an entry count.
            if (type != 0 || count != 0)
            {
                result.Add(new MbrPartition(slot, type, entry[0] == 0x80, BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), count));
            }
        }

        return result;
    }

    private sealed record GptInfo(bool HeaderValid, long BackupSector, long LastUsable, Guid DiskId, List<GptPartition> Partitions);

    private static GptInfo? TryReadGpt(Stream stream, int sectorSize)
    {
        var header = new byte[92];
        if (ReadAt(stream, sectorSize, header) < header.Length || !header.AsSpan(0, 8).SequenceEqual(GptSignature))
        {
            return null;
        }

        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        var valid = headerSize is >= 92 and <= 4096 && HeaderChecksumMatches(stream, sectorSize, headerSize);
        var entriesLba = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72));
        var count = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80)), MaxGptEntries);
        var entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84));

        var partitions = new List<GptPartition>();
        if (entrySize >= 128 && entrySize <= 4096 && entriesLba > 0)
        {
            var table = new byte[count * entrySize];
            var read = ReadAt(stream, entriesLba * sectorSize, table);
            for (var i = 0; i < count && (i + 1) * entrySize <= read; i++)
            {
                var entry = table.AsSpan(i * entrySize, entrySize);
                var type = new Guid(entry[..16]);
                if (type == Guid.Empty)
                {
                    continue;
                }

                partitions.Add(new GptPartition(
                    type,
                    new Guid(entry.Slice(16, 16)),
                    (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]),
                    (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]),
                    Encoding.Unicode.GetString(entry.Slice(56, 72)).TrimEnd('\0')));
            }
        }

        return new GptInfo(
            valid,
            (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)),
            (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48)),
            new Guid(header.AsSpan(56, 16)),
            partitions);
    }

    /// <summary>The header CRC is computed over <c>headerSize</c> bytes with the CRC field itself set to zero.</summary>
    private static bool HeaderChecksumMatches(Stream stream, int sectorSize, int headerSize)
    {
        var full = new byte[headerSize];
        if (ReadAt(stream, sectorSize, full) < headerSize)
        {
            return false;
        }

        var stored = BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(16));
        full.AsSpan(16, 4).Clear();
        return Crc32.HashToUInt32(full) == stored;
    }

    private static int ReadAt(Stream stream, long offset, byte[] buffer)
    {
        if (offset < 0 || offset >= stream.Length)
        {
            return 0;
        }

        stream.Position = offset;
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
    }
}
