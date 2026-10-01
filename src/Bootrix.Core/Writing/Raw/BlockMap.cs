// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;

namespace Bootrix.Core.Writing.Raw;

/// <summary>A run of blocks of the image, both ends included, with the checksum of its contents when the file has one.</summary>
public sealed record BlockMapRange(long FirstBlock, long LastBlock, string? Checksum);

/// <summary>
/// A block map as written by bmaptool: which blocks of an image carry data. Everything else is a hole and reads
/// as zeros in the image, so a writer may leave it alone.
/// </summary>
public sealed record BlockMap
{
    public required string Version { get; init; }

    /// <summary>Size of the decoded image; null in files that leave it out.</summary>
    public long? ImageSize { get; init; }

    public required int BlockSize { get; init; }

    public long BlocksCount { get; init; }

    public long MappedBlocksCount { get; init; }

    /// <summary>Hash used for the range checksums: "sha256", "sha1" (all of format 1.x) and so on.</summary>
    public required string ChecksumType { get; init; }

    /// <summary>Whether the checksum the file records for itself matches; null when it records none.</summary>
    public bool? FileChecksumValid { get; init; }

    public required IReadOnlyList<BlockMapRange> Ranges { get; init; }

    public long MappedBytes => MappedBlocksCount * BlockSize;

    /// <summary>The ranges in bytes, cut off at the end of the image (the last block may be partial).</summary>
    public IReadOnlyList<ByteRange> ToByteRanges()
    {
        var limit = ImageSize ?? long.MaxValue;
        var result = new List<ByteRange>(Ranges.Count);
        foreach (var range in Ranges)
        {
            var start = range.FirstBlock * BlockSize;
            var end = Math.Min((range.LastBlock + 1) * BlockSize, limit);
            if (end > start)
            {
                result.Add(new ByteRange(start, end - start));
            }
        }

        return result;
    }

    public SparseWriteMap ToWriteMap(bool fillGaps) => new(ToByteRanges(), fillGaps);

    /// <summary>The file describes an image of this length; a block map that belongs to another image must not be used.</summary>
    public bool Describes(long imageLength) => ImageSize is not { } size || size == imageLength;
}
