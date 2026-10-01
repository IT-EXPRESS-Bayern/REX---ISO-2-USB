// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>openSUSE's <c>get.opensuse.org/api/v0/distributions.json</c>: which Leap releases are stable right now.</summary>
internal static class OpenSuseReleases
{
    /// <summary>Leap releases the project calls "Stable", newest first. Release candidates and end-of-life releases are not offered.</summary>
    public static IReadOnlyList<NumericVersion> StableLeap(string json)
    {
        using var document = DistroJson.Parse(json, "openSUSE distributions");
        if (document.RootElement.Child("Leap") is not { ValueKind: JsonValueKind.Array } leap)
        {
            return [];
        }

        var versions = new List<NumericVersion>();
        foreach (var entry in leap.EnumerateArray())
        {
            if (entry.String("state") == "Stable" && NumericVersion.TryParse(entry.String("version"), out var version))
            {
                versions.Add(version);
            }
        }

        return [.. versions.OrderByDescending(v => v)];
    }
}
