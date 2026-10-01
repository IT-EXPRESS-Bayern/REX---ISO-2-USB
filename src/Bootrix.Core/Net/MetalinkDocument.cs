// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Net;

/// <summary>
/// Reads Metalink 4 (RFC 5854, used by openSUSE's MirrorCache) and the older 3.0 format that Fedora's
/// MirrorManager still serves. Only parsing happens here; downloading is the engine's business.
/// </summary>
public sealed class MetalinkDocument
{
    private const string Namespace4 = "urn:ietf:params:xml:ns:metalink";
    private const string Namespace3 = "http://www.metalinker.org/";

    private MetalinkDocument(IReadOnlyList<MetalinkFile> files)
    {
        Files = files;
    }

    public IReadOnlyList<MetalinkFile> Files { get; }

    public static MetalinkDocument Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        XDocument document;
        try
        {
            // A downloaded metalink is untrusted input: no DTDs, no external entities.
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 32 * 1024 * 1024,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw Invalid(ex.Message, ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "metalink")
        {
            throw Invalid("root element is not <metalink>");
        }

        var ns = root.Name.Namespace;
        IEnumerable<XElement> fileElements;
        Func<XElement, XNamespace, MetalinkFile> read;
        if (ns == Namespace4)
        {
            fileElements = root.Elements(ns + "file");
            read = ReadVersion4;
        }
        else if (ns == Namespace3)
        {
            fileElements = root.Element(ns + "files")?.Elements(ns + "file") ?? [];
            read = ReadVersion3;
        }
        else
        {
            throw Invalid($"unknown namespace '{ns}'");
        }

        var files = fileElements.Select(e => read(e, ns)).ToList();
        return files.Count == 0 ? throw Invalid("no <file> entries") : new MetalinkDocument(files);
    }

    public MetalinkFile? Find(string name) =>
        Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    private static MetalinkFile ReadVersion4(XElement file, XNamespace ns)
    {
        var name = RequiredName(file);
        var size = ReadSize(file.Element(ns + "size"), name);
        var hashes = ReadHashes(file.Elements(ns + "hash"), name);

        var pieces = file.Element(ns + "pieces");
        var pieceList = pieces is null
            ? []
            : ReadPieces(size, pieces, pieces.Elements(ns + "hash").Select(h => h.Value.Trim()).ToList(), name);

        var mirrors = file.Elements(ns + "url")
            .Select(u => CreateMirror(u.Value, ParsePriority((string?)u.Attribute("priority"), 999_999, name), (string?)u.Attribute("location"), 0))
            .OfType<MirrorSource>();

        return new MetalinkFile(name, size, hashes, pieceList, [.. mirrors.OrderBy(m => m.Priority)]);
    }

    private static MetalinkFile ReadVersion3(XElement file, XNamespace ns)
    {
        var name = RequiredName(file);
        var size = ReadSize(file.Element(ns + "size"), name);
        var verification = file.Element(ns + "verification");
        var hashes = ReadHashes(verification?.Elements(ns + "hash") ?? [], name);

        var pieces = verification?.Element(ns + "pieces");
        IReadOnlyList<PieceHash> pieceList = [];
        if (pieces is not null)
        {
            var ordered = pieces.Elements(ns + "hash")
                .OrderBy(h => int.TryParse((string?)h.Attribute("piece"), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : 0)
                .Select(h => h.Value.Trim())
                .ToList();
            pieceList = ReadPieces(size, pieces, ordered, name);
        }

        var resources = file.Element(ns + "resources");
        var defaultLimit = ParseCount((string?)resources?.Attribute("maxconnections"));
        var mirrors = (resources?.Elements(ns + "url") ?? [])
            .Select(u =>
            {
                // Preference runs from 1 to 100 with 100 best; Metalink 4 priorities are the other way round.
                var preference = ParsePriority((string?)u.Attribute("preference"), 100, name);
                var limit = ParseCount((string?)u.Attribute("maxconnections"));
                return CreateMirror(u.Value, Math.Max(1, 101 - preference), (string?)u.Attribute("location"), limit > 0 ? limit : defaultLimit);
            })
            .OfType<MirrorSource>();

        return new MetalinkFile(name, size, hashes, pieceList, [.. mirrors.OrderBy(m => m.Priority)]);
    }

    private static string RequiredName(XElement file) =>
        string.IsNullOrWhiteSpace((string?)file.Attribute("name")) ? throw Invalid("<file> without name") : (string)file.Attribute("name")!;

    private static long? ReadSize(XElement? element, string file)
    {
        if (element is null)
        {
            return null;
        }

        return long.TryParse(element.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? size
            : throw Invalid($"{file}: bad <size>");
    }

    private static List<FileHash> ReadHashes(IEnumerable<XElement> elements, string file)
    {
        var hashes = new List<FileHash>();
        foreach (var element in elements)
        {
            // Algorithms we cannot compute (sha-224, ripemd160, ...) are not an error, just not usable.
            if (!HashKinds.TryParseName((string?)element.Attribute("type") ?? string.Empty, out var kind))
            {
                continue;
            }

            hashes.Add(FileHash.TryCreate(kind, element.Value.Trim(), out var hash)
                ? hash!
                : throw Invalid($"{file}: malformed {kind} digest"));
        }

        return hashes;
    }

    private static IReadOnlyList<PieceHash> ReadPieces(long? size, XElement pieces, List<string> digests, string file)
    {
        if (!HashKinds.TryParseName((string?)pieces.Attribute("type") ?? string.Empty, out var kind) || !kind.IsAcceptedForVerification())
        {
            return [];
        }

        if (!long.TryParse((string?)pieces.Attribute("length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length <= 0)
        {
            throw Invalid($"{file}: bad piece length");
        }

        if (size is null)
        {
            return [];
        }

        try
        {
            return PieceHash.CreateUniform(size.Value, length, kind, digests);
        }
        catch (ArgumentException ex)
        {
            throw Invalid($"{file}: {ex.Message}", ex);
        }
    }

    private static MirrorSource? CreateMirror(string text, int priority, string? location, int maxConnections)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return new MirrorSource(url, priority, string.IsNullOrWhiteSpace(location) ? null : location.ToUpperInvariant(), maxConnections);
    }

    private static int ParsePriority(string? text, int fallback, string file) =>
        text is null
            ? fallback
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw Invalid($"{file}: bad priority '{text}'");

    private static int ParseCount(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static BootrixException Invalid(string detail, Exception? inner = null) =>
        new(ErrorCode.MetalinkInvalid, detail, inner) { Arguments = [detail] };
}
