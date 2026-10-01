// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Net;

/// <summary>One digest from a checksum file. <paramref name="FileName"/> is null for files that contain only a hash.</summary>
public sealed record ChecksumEntry(FileHash Hash, string? FileName);

/// <summary>
/// Reads the checksum formats distributions publish: GNU (<c>hash *file</c>, <c>hash  file</c>), BSD
/// (<c>SHA256 (file) = hash</c>), a bare hash, and files split into <c>### SHA256SUMS:</c> sections.
/// PGP clear-signing armor around the lines is skipped; verifying it is the caller's job.
/// </summary>
public sealed partial class ChecksumFile
{
    private ChecksumFile(IReadOnlyList<ChecksumEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<ChecksumEntry> Entries { get; }

    public static ChecksumFile Parse(ReadOnlySpan<byte> utf8) =>
        Parse(Encoding.UTF8.GetString(utf8).TrimStart('\uFEFF'));

    public static ChecksumFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<ChecksumEntry>();
        var armor = ArmorState.None;
        var section = SectionAlgorithm.None;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            switch (armor)
            {
                case ArmorState.Signature:
                    if (line.StartsWith("-----END PGP SIGNATURE-----", StringComparison.Ordinal))
                    {
                        armor = ArmorState.None;
                    }

                    continue;
                case ArmorState.Headers:
                    if (line.Length == 0)
                    {
                        armor = ArmorState.Text;
                    }

                    continue;
            }

            if (line.StartsWith("-----BEGIN PGP SIGNED MESSAGE-----", StringComparison.Ordinal))
            {
                armor = ArmorState.Headers;
                continue;
            }

            if (line.StartsWith("-----BEGIN PGP SIGNATURE-----", StringComparison.Ordinal))
            {
                armor = ArmorState.Signature;
                continue;
            }

            // Clear-signing escapes lines that start with a dash.
            if (armor == ArmorState.Text && line.StartsWith("- ", StringComparison.Ordinal))
            {
                line = line[2..];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var header = SectionHeader().Match(line);
            if (header.Success)
            {
                section = HashKinds.TryParseName(header.Groups["name"].Value, out var kind)
                    ? new SectionAlgorithm(true, kind)
                    : new SectionAlgorithm(true, null);
                continue;
            }

            if (line[0] is '#' or ';')
            {
                continue;
            }

            if (section is { InSection: true, Kind: null })
            {
                // BLAKE2/BLAKE3 sections use the same digest lengths as SHA-512/SHA-256 and must not be mistaken for them.
                continue;
            }

            if (TryParseLine(line, section.Kind, out var entry))
            {
                entries.Add(entry!);
            }
        }

        return new ChecksumFile(entries);
    }

    /// <summary>All entries that plausibly describe <paramref name="fileName"/>, best match tier first.</summary>
    public IReadOnlyList<ChecksumEntry> Find(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var named = Entries.Where(e => e.FileName is not null).ToList();
        if (named.Count == 0)
        {
            return Entries;
        }

        var wanted = NormalizeName(fileName);
        foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
        {
            var exact = named.Where(e => string.Equals(e.FileName, wanted, comparison)).ToList();
            if (exact.Count > 0)
            {
                return exact;
            }

            // A path prefix such as "v8.10/memtest.iso" in the file, or a longer path on our side, still names the same file.
            var byPath = named.Where(e => IsPathSuffix(e.FileName!, wanted, comparison) || IsPathSuffix(wanted, e.FileName!, comparison)).ToList();
            if (byPath.Count > 0)
            {
                return byPath;
            }
        }

        return [];
    }

    /// <summary>
    /// Picks the digest for <paramref name="fileName"/>: the requested algorithm, otherwise the strongest one
    /// that may be used for verification. Missing and contradicting entries are errors.
    /// </summary>
    public FileHash Select(string fileName, HashKind? kind = null)
    {
        var matches = Find(fileName)
            .Where(e => kind is null ? e.Hash.Kind.IsAcceptedForVerification() : e.Hash.Kind == kind)
            .ToList();

        if (matches.Count == 0)
        {
            throw Invalid(fileName, "no matching entry");
        }

        var strongest = matches.Max(e => e.Hash.Kind);
        var candidates = matches.Where(e => e.Hash.Kind == strongest).Select(e => e.Hash).Distinct().ToList();
        return candidates.Count == 1 ? candidates[0] : throw Invalid(fileName, "conflicting entries");
    }

    public bool TryGetHash(string fileName, HashKind kind, out FileHash? hash)
    {
        var candidates = Find(fileName).Select(e => e.Hash).Where(h => h.Kind == kind).Distinct().ToList();
        hash = candidates.Count == 1 ? candidates[0] : null;
        return hash is not null;
    }

    private static BootrixException Invalid(string fileName, string reason) =>
        new(ErrorCode.ChecksumFileInvalid, $"{fileName}: {reason}") { Arguments = [fileName] };

    private static bool TryParseLine(string line, HashKind? sectionKind, out ChecksumEntry? entry)
    {
        entry = null;

        var bsd = BsdLine().Match(line);
        if (bsd.Success)
        {
            if (!HashKinds.TryParseName(bsd.Groups["tag"].Value, out var tagKind))
            {
                return false;
            }

            return TryCreate(tagKind, bsd.Groups["hex"].Value, bsd.Groups["name"].Value, out entry);
        }

        var span = line.AsSpan();
        var escaped = span.StartsWith("\\");
        if (escaped)
        {
            span = span[1..];
        }

        var hexLength = 0;
        while (hexLength < span.Length && char.IsAsciiHexDigit(span[hexLength]))
        {
            hexLength++;
        }

        var kind = sectionKind ?? HashKinds.FromHexLength(hexLength);
        if (hexLength == 0 || kind is null)
        {
            return false;
        }

        var hex = span[..hexLength].ToString();
        var rest = span[hexLength..];
        if (rest.IsEmpty)
        {
            return TryCreate(kind.Value, hex, null, out entry);
        }

        if (rest[0] is not (' ' or '\t'))
        {
            return false;
        }

        // " *" marks binary mode, "  " text mode; neither is part of the name.
        var name = rest.TrimStart(" \t").ToString();
        if (name.StartsWith('*'))
        {
            name = name[1..];
        }

        if (escaped)
        {
            name = UnescapeGnu(name);
        }

        return name.Length > 0 && TryCreate(kind.Value, hex, name, out entry);
    }

    private static bool TryCreate(HashKind kind, string hex, string? name, out ChecksumEntry? entry)
    {
        if (!FileHash.TryCreate(kind, hex, out var hash))
        {
            entry = null;
            return false;
        }

        entry = new ChecksumEntry(hash!, name is null ? null : NormalizeName(name));
        return true;
    }

    private static string UnescapeGnu(string name)
    {
        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '\\' && i + 1 < name.Length && name[i + 1] is '\\' or 'n')
            {
                builder.Append(name[++i] == 'n' ? '\n' : '\\');
            }
            else
            {
                builder.Append(name[i]);
            }
        }

        return builder.ToString();
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim().Replace('\\', '/');
        while (name.StartsWith("./", StringComparison.Ordinal))
        {
            name = name[2..];
        }

        return name.TrimStart('*');
    }

    private static bool IsPathSuffix(string path, string suffix, StringComparison comparison) =>
        path.Length > suffix.Length
        && path[path.Length - suffix.Length - 1] == '/'
        && path.EndsWith(suffix, comparison);

    [GeneratedRegex(@"^(?<tag>[A-Za-z0-9-]+)\s?\((?<name>.+)\)\s?=\s?(?<hex>[0-9A-Fa-f]+)$")]
    private static partial Regex BsdLine();

    [GeneratedRegex(@"^###\s*(?<name>[A-Za-z0-9_-]+?SUMS?)\s*:?\s*$")]
    private static partial Regex SectionHeader();

    private enum ArmorState
    {
        None,
        Headers,
        Text,
        Signature,
    }

    private readonly record struct SectionAlgorithm(bool InSection, HashKind? Kind)
    {
        public static SectionAlgorithm None => default;
    }
}
