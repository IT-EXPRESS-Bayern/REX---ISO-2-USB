// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Writing.Raw;

/// <summary>
/// Checks the decoded image against the range checksums of its block map while the writer reads it. Skipping blocks
/// on the strength of a map that belongs to a different image would write garbage, and the checksums are what
/// notices that, long before the read-back.
/// </summary>
public sealed class BlockMapVerifier : IDisposable
{
    private readonly BlockMap _map;
    private readonly HashAlgorithmName _algorithm;
    private readonly List<(long Start, long End, string Checksum)> _ranges;
    private IncrementalHash? _hash;
    private int _next;

    private BlockMapVerifier(BlockMap map, HashAlgorithmName algorithm)
    {
        _map = map;
        _algorithm = algorithm;
        var limit = map.ImageSize ?? long.MaxValue;
        _ranges =
        [
            .. map.Ranges
                .Where(range => range.Checksum is not null)
                .OrderBy(range => range.FirstBlock)
                .Select(range => (Start: range.FirstBlock * map.BlockSize, End: Math.Min((range.LastBlock + 1) * map.BlockSize, limit), Checksum: range.Checksum!))
                .Where(range => range.End > range.Start),
        ];
    }

    /// <summary>Null when the map has no checksums or uses a hash this build does not know.</summary>
    public static BlockMapVerifier? Create(BlockMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (BlockMapParser.HashFor(map.ChecksumType) is not { } algorithm || !map.Ranges.Any(range => range.Checksum is not null))
        {
            return null;
        }

        return new BlockMapVerifier(map, algorithm);
    }

    /// <summary>Number of ranges whose checksum has been compared so far.</summary>
    public int RangesVerified { get; private set; }

    /// <summary>Feeds the next piece of the image; pieces must arrive in order and without gaps.</summary>
    public void Observe(long offset, ReadOnlySpan<byte> data)
    {
        var end = offset + data.Length;
        while (_next < _ranges.Count)
        {
            var (start, rangeEnd, checksum) = _ranges[_next];
            if (start >= end)
            {
                break;
            }

            var from = Math.Max(start, offset);
            var to = Math.Min(rangeEnd, end);
            if (to > from)
            {
                _hash ??= IncrementalHash.CreateHash(_algorithm);
                _hash.AppendData(data[(int)(from - offset)..(int)(to - offset)]);
            }

            if (rangeEnd > end)
            {
                break;
            }

            Finish(start, checksum);
            _next++;
        }
    }

    /// <summary>Called when the image has been read completely; a range that ends beyond the data cannot be checked.</summary>
    public void Complete(long imageLength)
    {
        if (_next < _ranges.Count && _ranges[_next].Start < imageLength)
        {
            throw Mismatch(_ranges[_next].Start, "the image ends inside a mapped range");
        }

        if (_map.ImageSize is { } size && size != imageLength)
        {
            throw Mismatch(0, $"the image has {imageLength} bytes, the block map describes {size}");
        }
    }

    public void Dispose() => _hash?.Dispose();

    private void Finish(long start, string expected)
    {
        var actual = Convert.ToHexStringLower(_hash!.GetHashAndReset());
        _hash.Dispose();
        _hash = null;
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw Mismatch(start, "a mapped range does not match its checksum");
        }

        RangesVerified++;
    }

    private static BootrixException Mismatch(long offset, string detail) =>
        new(ErrorCode.ImageHashMismatch, $"{detail} (offset {offset})");
}
