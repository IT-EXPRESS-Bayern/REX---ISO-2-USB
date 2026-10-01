// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Images.Wim;

/// <summary>
/// What a WIM, ESD or split WIM part says about itself: header fields plus the images listed in its XML
/// data. Only the header and the XML resource are read, never the file contents, so a multi-gigabyte
/// install.wim is analysed with two small reads. No DISM or wimlib is involved.
/// </summary>
public sealed record WimMetadata
{
    private const int MaxXmlBytes = 64 * 1024 * 1024;

    public required WimHeader Header { get; init; }

    public required IReadOnlyList<WimEdition> Editions { get; init; }

    /// <summary>Length of the file the metadata was read from.</summary>
    public long FileLength { get; init; }

    /// <summary>The TOTALBYTES recorded for the WIM itself: its size when it was written (all parts together for a split WIM).</summary>
    public long? RecordedWimBytes { get; init; }

    /// <summary>Sum of the uncompressed sizes of all images.</summary>
    public long ImageBytes => Editions.Sum(edition => edition.TotalBytes);

    /// <summary>The architecture shared by all images; unknown when they differ or carry no Windows data.</summary>
    public WindowsArch Arch
    {
        get
        {
            var architectures = Editions.Select(edition => edition.Arch).Where(arch => arch != WindowsArch.Unknown).Distinct().ToList();
            return architectures.Count == 1 ? architectures[0] : WindowsArch.Unknown;
        }
    }

    /// <summary>The highest build number among the images, 0 when none has version data.</summary>
    public int Build => Editions.Select(edition => edition.Build).DefaultIfEmpty().Max();

    public IReadOnlyList<string> Languages => [.. Editions.SelectMany(edition => edition.Languages).Distinct(StringComparer.OrdinalIgnoreCase)];

    public string? DefaultLanguage => Editions.Select(edition => edition.DefaultLanguage).FirstOrDefault(language => language is not null);

    /// <summary>True for boot.wim style files that contain only Windows PE and Setup images.</summary>
    public bool IsBootImage => Editions.Count > 0 && Editions.All(edition => edition.IsBootImage);

    public static WimMetadata Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = stream.Length;
        var buffer = new byte[WimHeader.Size];
        stream.Position = 0;
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
        {
            throw ImageErrors.Unreadable("The file is shorter than a WIM header.");
        }

        var header = WimHeader.Parse(buffer);
        var xml = ValidateXmlResource(header, length);
        if (xml is null)
        {
            return Create(header, length, string.Empty);
        }

        var data = new byte[xml.Value.StoredSize];
        stream.Position = xml.Value.Offset;
        stream.ReadExactly(data);
        return Create(header, length, DecodeXml(data));
    }

    public static async Task<WimMetadata> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = stream.Length;
        var buffer = new byte[WimHeader.Size];
        stream.Position = 0;
        var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read < buffer.Length)
        {
            throw ImageErrors.Unreadable("The file is shorter than a WIM header.");
        }

        var header = WimHeader.Parse(buffer);
        var xml = ValidateXmlResource(header, length);
        if (xml is null)
        {
            return Create(header, length, string.Empty);
        }

        var data = new byte[xml.Value.StoredSize];
        stream.Position = xml.Value.Offset;
        await stream.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return Create(header, length, DecodeXml(data));
    }

    /// <summary>The XML resource, or null when the header names none (a part without image data).</summary>
    private static WimResource? ValidateXmlResource(WimHeader header, long fileLength)
    {
        var xml = header.XmlData;
        if (xml.IsEmpty)
        {
            return null;
        }

        if (xml.End > fileLength || xml.Offset < WimHeader.Size)
        {
            throw ImageErrors.Truncated(header.ExpectedLength, fileLength);
        }

        // Microsoft's and wimlib's writers store the XML uncompressed; a compressed one would need the
        // chunk table and the codec, which are only available for the file contents.
        if (xml.IsCompressed)
        {
            throw ImageErrors.Unsupported("a WIM with a compressed XML resource");
        }

        if (xml.StoredSize > MaxXmlBytes)
        {
            throw ImageErrors.Unreadable("The XML data of the WIM is implausibly large.");
        }

        return xml;
    }

    private static string DecodeXml(byte[] data) =>
        Encoding.Unicode.GetString(data, 0, data.Length & ~1);

    private static WimMetadata Create(WimHeader header, long fileLength, string xml)
    {
        if (xml.Length == 0)
        {
            return new WimMetadata { Header = header, Editions = [], FileLength = fileLength };
        }

        var (editions, totalBytes) = WimXmlParser.Parse(xml);
        return new WimMetadata
        {
            Header = header,
            Editions = editions,
            FileLength = fileLength,
            RecordedWimBytes = totalBytes,
        };
    }
}
