// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;

namespace Bootrix.Core.FileSystems.Fat;

/// <summary>
/// Writes an empty FAT12, FAT16 or FAT32 file system into a stream, independent of what Windows
/// is willing to format. Only metadata is written (boot sectors, FATs, root directory), so a
/// sparse target stays sparse when <see cref="FatFormatOptions.AssumeZeroed"/> is set.
/// The volume starts at the stream's offset zero; wrap a partition in a slice to place it elsewhere.
/// </summary>
public static class FatFormatter
{
    private const int ZeroChunkBytes = 1 << 20;
    private const byte VolumeLabelAttribute = 0x08;

    public static FatFormatResult Format(Stream target, FatFormatOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        if (!target.CanSeek || !target.CanWrite)
        {
            throw new ArgumentException("The target must be seekable and writable.", nameof(target));
        }

        var layout = FatGeometry.Compute(options);
        var volumeBytes = layout.TotalSectors * layout.BytesPerSector;
        if (target.Length < volumeBytes)
        {
            var detail = $"the target holds {target.Length} bytes but the volume needs {volumeBytes}";
            throw new BootrixException(ErrorCode.InvalidSpec, detail) { Arguments = [detail] };
        }

        var now = (options.TimeProvider ?? TimeProvider.System).GetLocalNow();
        var volumeId = options.VolumeId ?? FatVolumeId.FromTime(now);
        var label = FatLabel.Normalize(options.Label);

        if (!options.AssumeZeroed)
        {
            ZeroMetadata(target, layout, cancellationToken);
        }

        WriteAt(target, 0, FatBootSector.BuildReservedArea(layout, options, volumeId, label));
        WriteFats(target, layout, options.MediaDescriptor);
        WriteRootDirectory(target, layout, label, now);
        target.Flush();

        return new FatFormatResult(layout, volumeId, label);
    }

    /// <summary>Everything a driver reads before the first file: reserved area, FATs and the root directory.</summary>
    private static void ZeroMetadata(Stream target, FatLayout layout, CancellationToken cancellationToken)
    {
        var endSector = layout.DataStartSector + (layout.Type == FatType.Fat32 ? layout.SectorsPerCluster : 0);
        var remaining = endSector * layout.BytesPerSector;
        var chunk = new byte[(int)Math.Min(remaining, ZeroChunkBytes)];

        target.Position = 0;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(remaining, chunk.Length);
            target.Write(chunk, 0, count);
            remaining -= count;
        }
    }

    private static void WriteFats(Stream target, FatLayout layout, byte media)
    {
        var first = new byte[layout.BytesPerSector];
        switch (layout.Type)
        {
            case FatType.Fat12:
                // Entry 0 is 0xF00 | media, entry 1 is end-of-chain: three bytes for the two 12-bit values.
                first[0] = media;
                first[1] = 0xFF;
                first[2] = 0xFF;
                break;
            case FatType.Fat16:
                first[0] = media;
                first.AsSpan(1, 3).Fill(0xFF);
                break;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(first, 0x0FFFFF00u | media);
                BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(4), 0x0FFFFFFF);

                // Cluster 2 is the root directory and a single cluster long.
                BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(8), 0x0FFFFFFF);
                break;
        }

        for (var copy = 0; copy < layout.FatCount; copy++)
        {
            WriteAt(target, (layout.ReservedSectors + copy * layout.SectorsPerFat) * layout.BytesPerSector, first);
        }
    }

    private static void WriteRootDirectory(Stream target, FatLayout layout, string label, DateTimeOffset now)
    {
        if (label.Length == 0)
        {
            return;
        }

        var (date, time) = FatTimestamp.Encode(now);
        var sector = new byte[layout.BytesPerSector];
        FatLabel.ToField(label).CopyTo(sector, 0);
        sector[11] = VolumeLabelAttribute;
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(14), time);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(16), date);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(18), date);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(22), time);
        BinaryPrimitives.WriteUInt16LittleEndian(sector.AsSpan(24), date);

        var startSector = layout.Type == FatType.Fat32 ? layout.DataStartSector : layout.RootDirectoryStartSector;
        WriteAt(target, startSector * layout.BytesPerSector, sector);
    }

    private static void WriteAt(Stream target, long offset, byte[] data)
    {
        target.Position = offset;
        target.Write(data, 0, data.Length);
    }
}
