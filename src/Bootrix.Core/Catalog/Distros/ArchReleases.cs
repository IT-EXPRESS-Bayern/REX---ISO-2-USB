// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="IsoPath">Path below a mirror root, "iso/2026.09.01/archlinux-2026.09.01-x86_64.iso".</param>
/// <param name="Sha256">The digest archlinux.org publishes in its release list.</param>
/// <param name="PgpFingerprint">Key that signed the release; the detached .sig next to the ISO is made with it.</param>
internal sealed record ArchRelease(
    string Version,
    DateOnly? ReleaseDate,
    string? KernelVersion,
    string IsoPath,
    FileHash Sha256,
    long? Size,
    string? PgpFingerprint)
{
    public string FileName => IsoPath[(IsoPath.LastIndexOf('/') + 1)..];
}

/// <summary>Arch Linux's release list (<c>archlinux.org/releng/releases/json/</c>) and its mirror status page.</summary>
internal static class ArchReleases
{
    /// <summary>The releases still offered for download, newest first. Older entries stay in the list but are flagged unavailable.</summary>
    public static IReadOnlyList<ArchRelease> Parse(string json)
    {
        using var document = DistroJson.Parse(json, "Arch Linux releases");
        if (document.RootElement.Child("releases") is not { ValueKind: JsonValueKind.Array } releases)
        {
            return [];
        }

        var result = new List<ArchRelease>();
        foreach (var entry in releases.EnumerateArray())
        {
            if (!(entry.Child("available") is { ValueKind: JsonValueKind.True })
                || entry.String("version") is not { Length: > 0 } version
                || entry.String("iso_url") is not { Length: > 0 } isoUrl
                || !FileHash.TryCreate(HashKind.Sha256, entry.String("sha256_sum"), out var sha256))
            {
                continue;
            }

            DateOnly? date = DateOnly.TryParseExact(entry.String("release_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
            var size = entry.Child("torrent") is { } torrent ? torrent.Number("file_length") : null;

            result.Add(new ArchRelease(version, date, entry.String("kernel_version"), isoUrl.TrimStart('/'), sha256!, size, entry.String("pgp_fingerprint")));
        }

        return [.. result.OrderByDescending(r => r.Version, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Mirrors that serve ISOs over HTTPS, are fully synchronised and answer well, best first. The status page lists
    /// more than a thousand entries (rsync, http, partial copies), so only the few that matter are taken.
    /// </summary>
    public static IReadOnlyList<MirrorSource> ParseMirrors(string json, int limit)
    {
        using var document = DistroJson.Parse(json, "Arch Linux mirror status");
        if (document.RootElement.Child("urls") is not { ValueKind: JsonValueKind.Array } urls)
        {
            return [];
        }

        var candidates = new List<(Uri Url, string? Country, double Score)>();
        foreach (var mirror in urls.EnumerateArray())
        {
            if (mirror.String("protocol") != "https"
                || mirror.Child("isos") is not { ValueKind: JsonValueKind.True }
                || mirror.Child("active") is not { ValueKind: JsonValueKind.True }
                || mirror.Child("completion_pct") is not { ValueKind: JsonValueKind.Number } completion || completion.GetDouble() < 0.999
                || mirror.Child("score") is not { ValueKind: JsonValueKind.Number } score
                || !Uri.TryCreate(mirror.String("url"), UriKind.Absolute, out var url))
            {
                continue;
            }

            candidates.Add((url, mirror.String("country_code"), score.GetDouble()));
        }

        // Priorities count up from 2; the vendor's own geo-routed address takes 1.
        return [.. candidates
            .OrderBy(c => c.Score)
            .Take(limit)
            .Select((c, index) => new MirrorSource(c.Url, 2 + index, c.Country))];
    }
}
