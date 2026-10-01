// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Apple;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// The validated chunk map of a UDIF image. Everything an untrusted block table can lie about
/// (sector counts, offsets, overlaps, sizes) is checked here once, so the reader can trust the numbers.
/// </summary>
internal sealed class DmgLayout
{
    private const long MaxPropertyListBytes = 128L * 1024 * 1024;
    private const long MaxResourceForkBytes = 16L * 1024 * 1024;

    public required UdifTrailer Trailer { get; init; }

    /// <summary>All chunks with data or zero-fill semantics, sorted by volume position and free of overlaps.</summary>
    public required UdifChunk[] Chunks { get; init; }

    public required DmgPartitionInfo[] Partitions { get; init; }

    public required long VolumeSectors { get; init; }

    public required bool UsesResourceFork { get; init; }

    public static DmgLayout Read(RandomAccessSource source, DmgReaderOptions options, ILogger logger)
    {
        ContainerSniffer.ThrowIfUnsupported(ContainerSniffer.Detect(source));

        var trailer = UdifTrailer.Find(source)
            ?? throw ImageErrors.Corrupt("UDIF trailer ('koly') not found; the file is incomplete or not a disk image");
        if (trailer.SegmentCount > 1)
        {
            throw new BootrixException(ErrorCode.ImageSegmented, $"segment {trailer.SegmentNumber} of {trailer.SegmentCount}");
        }

        logger.LogDebug("UDIF trailer: flags {Flags:X}, variant {Variant}, {Sectors} sectors, front {Front}",
            trailer.Flags, trailer.ImageVariant, trailer.SectorCount, trailer.AtFront);

        try
        {
            return Build(source, trailer, options, logger);
        }
        catch (OverflowException)
        {
            throw ImageErrors.Corrupt("numeric overflow in the block table");
        }
    }

    private static DmgLayout Build(RandomAccessSource source, UdifTrailer trailer, DmgReaderOptions options, ILogger logger)
    {
        var maxSectors = options.MaxVolumeBytes / DmgReader.SectorSize;

        if (trailer.XmlLength == 0 && trailer.ResourceForkLength == 0)
        {
            return BuildRawStub(source, trailer, maxSectors);
        }

        var resources = ReadResources(source, trailer, out var usesResourceFork);
        var chunks = new List<UdifChunk>();
        var partitions = new List<DmgPartitionInfo>(resources.Count);
        var dataStart = checked(trailer.BaseOffset + ToLong(trailer.DataForkOffset));

        for (var index = 0; index < resources.Count; index++)
        {
            var table = UdifBlockTable.Parse(resources[index].Data);
            var firstChunk = chunks.Count;
            AddTableChunks(chunks, table, index, dataStart, source.Length, maxSectors, options);
            var count = chunks.Count - firstChunk;

            partitions.Add(new DmgPartitionInfo(
                resources[index].Id,
                resources[index].Name,
                DmgPartitionInfo.ExtractType(resources[index].Name),
                (long)table.StartSector,
                (long)table.SectorCount,
                count,
                table.Checksum));
        }

        chunks.Sort((a, b) => a.Sector.CompareTo(b.Sector));
        for (var i = 1; i < chunks.Count; i++)
        {
            if (chunks[i].Sector < chunks[i - 1].EndSector)
            {
                throw ImageErrors.Corrupt($"chunks overlap at sector {chunks[i].Sector}");
            }
        }

        var end = chunks.Count == 0 ? 0 : chunks[^1].EndSector;
        foreach (var partition in partitions)
        {
            end = Math.Max(end, partition.StartSector + partition.SectorCount);
        }

        // The trailer's count wins when it is larger: trailing free space has no chunks of its own.
        if (trailer.SectorCount > (ulong)maxSectors)
        {
            throw TooLarge(trailer.SectorCount);
        }

        var sectors = Math.Max(end, (long)trailer.SectorCount);

        if (chunks.Count == 0 && sectors == 0)
        {
            throw ImageErrors.Corrupt("image contains no data");
        }

        logger.LogDebug("UDIF image: {Partitions} partitions, {Chunks} chunks, {Sectors} sectors", partitions.Count, chunks.Count, sectors);
        return new DmgLayout
        {
            Trailer = trailer,
            Chunks = [.. chunks],
            Partitions = [.. partitions],
            VolumeSectors = sectors,
            UsesResourceFork = usesResourceFork,
        };
    }

    // Uncompressed read-write images (UDRW) are the raw volume followed by a trailer without any block table.
    private static DmgLayout BuildRawStub(RandomAccessSource source, UdifTrailer trailer, long maxSectors)
    {
        var dataLength = ToLong(trailer.DataForkLength);
        var sectors = trailer.SectorCount != 0 ? ToLong(trailer.SectorCount) : dataLength / DmgReader.SectorSize;
        if (sectors <= 0)
        {
            throw ImageErrors.Corrupt("image contains no data");
        }

        if (sectors > maxSectors)
        {
            throw TooLarge((ulong)sectors);
        }

        var offset = checked(trailer.BaseOffset + ToLong(trailer.DataForkOffset));
        var end = checked(offset + (sectors * DmgReader.SectorSize));
        if (end > source.Length)
        {
            throw ImageErrors.Truncated(end, source.Length);
        }

        var chunk = new UdifChunk(UdifChunkType.Raw, 0, sectors, offset, sectors * DmgReader.SectorSize, 0);
        return new DmgLayout
        {
            Trailer = trailer,
            Chunks = [chunk],
            Partitions = [new DmgPartitionInfo(0, "raw volume", null, 0, sectors, 1, default)],
            VolumeSectors = sectors,
            UsesResourceFork = false,
        };
    }

    private static List<BlkxResource> ReadResources(RandomAccessSource source, UdifTrailer trailer, out bool usesResourceFork)
    {
        usesResourceFork = trailer.XmlLength == 0;
        var (offset, length, limit) = usesResourceFork
            ? (trailer.ResourceForkOffset, trailer.ResourceForkLength, MaxResourceForkBytes)
            : (trailer.XmlOffset, trailer.XmlLength, MaxPropertyListBytes);

        if (length > (ulong)limit)
        {
            throw ImageErrors.Corrupt("block table directory is implausibly large");
        }

        var start = checked(trailer.BaseOffset + ToLong(offset));
        var end = checked(start + (long)length);
        if (end > source.Length)
        {
            throw ImageErrors.Truncated(end, source.Length);
        }

        var buffer = new byte[(int)length];
        source.ReadExactlyAt(start, buffer);

        var resources = usesResourceFork ? ResourceForkReader.ReadBlkx(buffer) : BlkxResource.ReadPlist(buffer);
        if (resources.Count == 0)
        {
            throw ImageErrors.Corrupt("image has no partitions");
        }

        return resources;
    }

    private static void AddTableChunks(
        List<UdifChunk> chunks,
        UdifBlockTable table,
        int partition,
        long dataStart,
        long fileLength,
        long maxSectors,
        DmgReaderOptions options)
    {
        if (table.StartSector > (ulong)maxSectors || table.SectorCount > (ulong)maxSectors)
        {
            throw TooLarge(Math.Max(table.StartSector, table.SectorCount));
        }

        var tableBase = checked(dataStart + ToLong(table.DataOffset));
        var maxChunkSectors = options.MaxChunkBytes / DmgReader.SectorSize;

        foreach (var entry in table.Entries)
        {
            var type = (UdifChunkType)entry.Type;
            if (type == UdifChunkType.Terminator)
            {
                break;
            }

            if (type == UdifChunkType.Comment || entry.SectorCount == 0)
            {
                continue;
            }

            if (entry.SectorNumber > table.SectorCount || entry.SectorCount > table.SectorCount - entry.SectorNumber)
            {
                throw ImageErrors.Corrupt("chunk lies outside its partition");
            }

            var sectors = (long)entry.SectorCount;
            var length = 0L;
            var offset = 0L;
            switch (type)
            {
                case UdifChunkType.ZeroFill:
                case UdifChunkType.Ignore:
                    break;
                case UdifChunkType.Raw:
                    length = ToLong(entry.CompressedLength);
                    if (length != sectors * DmgReader.SectorSize)
                    {
                        throw ImageErrors.Corrupt("raw chunk size does not match its sector count");
                    }

                    offset = DataOffset(tableBase, entry.CompressedOffset, length, fileLength);
                    break;
                case UdifChunkType.Adc:
                case UdifChunkType.Zlib:
                case UdifChunkType.Bzip2:
                case UdifChunkType.Lzfse:
                case UdifChunkType.Xz:
                    if (sectors > maxChunkSectors)
                    {
                        throw ImageErrors.Corrupt($"compressed chunk of {sectors} sectors exceeds the limit of {maxChunkSectors}");
                    }

                    // Encoders expand incompressible data only slightly, so the stored size is bounded by the decoded size.
                    length = ToLong(entry.CompressedLength);
                    var decoded = sectors * DmgReader.SectorSize;
                    if (length == 0 || length > decoded + (decoded >> 3) + 4096)
                    {
                        throw ImageErrors.Corrupt("compressed chunk has an implausible size");
                    }

                    offset = DataOffset(tableBase, entry.CompressedOffset, length, fileLength);
                    break;
                default:
                    throw ImageErrors.Unsupported($"UDIF chunk type 0x{entry.Type:X8}");
            }

            chunks.Add(new UdifChunk(type, checked((long)table.StartSector + (long)entry.SectorNumber), sectors, offset, length, partition));
        }
    }

    private static long DataOffset(long tableBase, ulong chunkOffset, long length, long fileLength)
    {
        var offset = checked(tableBase + ToLong(chunkOffset));
        var end = checked(offset + length);
        if (offset < 0 || end > fileLength)
        {
            throw ImageErrors.Truncated(end, fileLength);
        }

        return offset;
    }

    private static long ToLong(ulong value) =>
        value <= long.MaxValue ? (long)value : throw ImageErrors.Corrupt("value out of range in the image header");

    private static BootrixException TooLarge(ulong sectors) =>
        ImageErrors.Corrupt($"implausible volume size of {sectors} sectors");
}
