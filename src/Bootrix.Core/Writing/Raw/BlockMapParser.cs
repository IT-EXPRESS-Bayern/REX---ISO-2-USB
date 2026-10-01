// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Writing.Raw;

/// <summary>
/// Reads the XML block map that bmaptool writes next to an image (formats 1.0 to 2.0). The file is streamed, because
/// a fragmented 100 GB image yields hundreds of thousands of ranges.
/// </summary>
public static partial class BlockMapParser
{
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const int MaxRanges = 16_000_000;
    private const int MaxBlockSize = 1 << 30;

    public static BlockMap Parse(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                throw Invalid("the file is implausibly large");
            }

            return Parse(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BootrixException(ErrorCode.BlockMapInvalid, path, ex) { Arguments = [ex.Message] };
        }
    }

    public static BlockMap Parse(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        try
        {
            return Read(content);
        }
        catch (Exception ex) when (ex is XmlException or FormatException or OverflowException or InvalidOperationException)
        {
            throw Invalid(ex.Message, ex);
        }
    }

    private static BlockMap Read(byte[] content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaxFileBytes,
        };

        string? version = null, checksumType = null, fileChecksum = null;
        long? imageSize = null;
        long blockSize = 0, blocksCount = 0, mappedCount = 0;
        var ranges = new List<BlockMapRange>();

        using var stream = new MemoryStream(content, writable: false);
        using var reader = XmlReader.Create(stream, settings);
        // ReadElementContent* moves on to the node behind the element, so only the other cases advance by themselves.
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.LocalName)
            {
                case "ImageSize":
                    imageSize = ReadNumber(reader);
                    break;
                case "BlockSize":
                    blockSize = ReadNumber(reader);
                    break;
                case "BlocksCount":
                    blocksCount = ReadNumber(reader);
                    break;
                case "MappedBlocksCount":
                    mappedCount = ReadNumber(reader);
                    break;
                case "ChecksumType":
                    checksumType = reader.ReadElementContentAsString().Trim().ToLowerInvariant();
                    break;
                case "BmapFileChecksum" or "BmapFileSHA1":
                    fileChecksum = reader.ReadElementContentAsString().Trim();
                    break;
                case "Range":
                    if (ranges.Count >= MaxRanges)
                    {
                        throw Invalid("the file lists too many ranges");
                    }

                    // Format 1.x calls the attribute "sha1", later formats "chksum".
                    var checksum = reader.GetAttribute("chksum") ?? reader.GetAttribute("sha1");
                    ranges.Add(ParseRange(reader.ReadElementContentAsString(), checksum?.Trim()));
                    break;
                default:
                    if (reader.LocalName == "bmap")
                    {
                        version = reader.GetAttribute("version");
                    }

                    reader.Read();
                    break;
            }
        }

        if (version is null)
        {
            throw Invalid("the root element 'bmap' with a version is missing");
        }

        if (blockSize <= 0 || blockSize > MaxBlockSize)
        {
            throw Invalid($"block size {blockSize} is not usable");
        }

        // Format 1.x knows SHA-1 only and does not name it.
        checksumType ??= "sha1";
        var map = new BlockMap
        {
            Version = version,
            ImageSize = imageSize,
            BlockSize = (int)blockSize,
            BlocksCount = blocksCount,
            MappedBlocksCount = mappedCount,
            ChecksumType = checksumType,
            FileChecksumValid = fileChecksum is null ? null : FileChecksumMatches(content, fileChecksum, checksumType),
            Ranges = ranges,
        };
        Validate(map);
        return map;
    }

    private static void Validate(BlockMap map)
    {
        long mapped = 0;
        foreach (var range in map.Ranges)
        {
            if (range.FirstBlock < 0 || range.LastBlock < range.FirstBlock || (map.BlocksCount > 0 && range.LastBlock >= map.BlocksCount))
            {
                throw Invalid($"range {range.FirstBlock}-{range.LastBlock} lies outside the image");
            }

            mapped += range.LastBlock - range.FirstBlock + 1;
        }

        // Ranges that overlap would be counted twice; bmaptool never writes them.
        if (mapped != map.MappedBlocksCount)
        {
            throw Invalid($"the ranges cover {mapped} blocks, the header says {map.MappedBlocksCount}");
        }

        if (map.ImageSize is { } size && map.BlocksCount > 0 && (size + map.BlockSize - 1) / map.BlockSize != map.BlocksCount)
        {
            throw Invalid("image size and block count do not agree");
        }
    }

    private static long ReadNumber(XmlReader reader)
    {
        var text = reader.ReadElementContentAsString().Trim();
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw Invalid($"'{text}' is not a number");
    }

    private static BlockMapRange ParseRange(string text, string? checksum)
    {
        var span = text.AsSpan().Trim();
        var dash = span.IndexOf('-');
        var first = dash < 0 ? span : span[..dash];
        var last = dash < 0 ? span : span[(dash + 1)..];
        if (!long.TryParse(first.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var from)
            || !long.TryParse(last.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var to))
        {
            throw Invalid($"'{text.Trim()}' is not a block range");
        }

        return new BlockMapRange(from, to, string.IsNullOrEmpty(checksum) ? null : checksum);
    }

    /// <summary>
    /// The file's own checksum is taken over the file with that value replaced by zeros of the same length, so the
    /// checksum can be stored in the file it protects.
    /// </summary>
    private static bool? FileChecksumMatches(byte[] content, string recorded, string type)
    {
        if (HashFor(type) is not { } algorithm)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(content);
        var match = ChecksumElement().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups[1];
        var zeroed = text.Remove(value.Index, value.Length).Insert(value.Index, new string('0', value.Length));
        var actual = Convert.ToHexStringLower(CryptographicOperations.HashData(algorithm, Encoding.UTF8.GetBytes(zeroed)));
        return actual.Equals(recorded, StringComparison.OrdinalIgnoreCase);
    }

    internal static HashAlgorithmName? HashFor(string type) => type switch
    {
        "sha1" => HashAlgorithmName.SHA1,
        "sha256" => HashAlgorithmName.SHA256,
        "sha384" => HashAlgorithmName.SHA384,
        "sha512" => HashAlgorithmName.SHA512,
        "md5" => HashAlgorithmName.MD5,
        _ => null,
    };

    private static BootrixException Invalid(string detail, Exception? inner = null) =>
        new(ErrorCode.BlockMapInvalid, detail, inner) { Arguments = [detail] };

    [GeneratedRegex(@"<(?:BmapFileChecksum|BmapFileSHA1)>\s*([0-9A-Fa-f]+)\s*</", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumElement();
}
