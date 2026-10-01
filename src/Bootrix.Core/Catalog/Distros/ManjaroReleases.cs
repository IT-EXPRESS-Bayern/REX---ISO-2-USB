// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Signature">Detached signature of the ISO itself; not usable before the download is complete.</param>
internal sealed record ManjaroImage(Uri Image, Uri Checksum, Uri? Signature, string FileName, string? Version, DateOnly? Date);

/// <param name="Group">"official" or "community".</param>
/// <param name="CustomUrl">Spins with their own download site have no image entry, only a link to that site.</param>
internal sealed record ManjaroEdition(string Key, string Group, ManjaroImage? Full, ManjaroImage? Minimal, Uri? CustomUrl);

/// <summary>Manjaro's <c>iso-info/file-info.json</c>, from which manjaro.org builds its download page.</summary>
internal static partial class ManjaroReleases
{
    private static readonly string[] Groups = ["official", "community"];

    /// <summary>The PC editions; the "arm" section lists single-board computers and is not boot media for a PC.</summary>
    public static IReadOnlyList<ManjaroEdition> Parse(string json)
    {
        using var document = DistroJson.Parse(json, "Manjaro file-info.json");

        var editions = new List<ManjaroEdition>();
        foreach (var group in Groups)
        {
            if (document.RootElement.Child(group) is not { ValueKind: JsonValueKind.Object } section)
            {
                continue;
            }

            foreach (var entry in section.EnumerateObject())
            {
                var full = ReadImage(entry.Value);
                var minimal = entry.Value.Child("minimal") is { } minimalEntry ? ReadImage(minimalEntry) : null;
                var custom = Uri.TryCreate(entry.Value.String("custom"), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;

                if (full is not null || minimal is not null || custom is not null)
                {
                    editions.Add(new ManjaroEdition(entry.Name, group, full, minimal, custom));
                }
            }
        }

        return editions;
    }

    private static ManjaroImage? ReadImage(JsonElement entry)
    {
        if (!IsHttps(entry.String("image"), out var image) || !IsHttps(entry.String("checksum"), out var checksum))
        {
            return null;
        }

        IsHttps(entry.String("signature"), out var signature);
        var fileName = Uri.UnescapeDataString(image!.Segments[^1]);
        var naming = ReleaseNaming().Match(fileName);

        DateOnly? date = naming.Success && DateOnly.TryParseExact(naming.Groups["date"].Value, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        return new ManjaroImage(image, checksum!, signature, fileName, naming.Success ? naming.Groups["version"].Value : null, date);
    }

    private static bool IsHttps(string? text, out Uri? url)
    {
        url = Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps ? parsed : null;
        return url is not null;
    }

    /// <summary>"manjaro-kde-26.1.1-minimal-260825-linux71.iso": edition, version, optional "minimal", build date, kernel.</summary>
    [GeneratedRegex(@"^manjaro-[a-z0-9]+-(?<version>\d+(?:\.\d+)*)(?:-minimal)?-(?<date>\d{6})-linux\d+\.iso$")]
    private static partial Regex ReleaseNaming();
}
