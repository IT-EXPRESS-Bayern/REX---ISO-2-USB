// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>Turns the build numbers in Microsoft's file and product names into the release names people know.</summary>
internal static partial class WindowsReleases
{
    /// <summary>End of regular servicing for Windows 10 22H2, the last version of Windows 10 (consumer extended updates run longer).</summary>
    public static readonly DateOnly Windows10EndOfSupport = new(2025, 10, 14);

    // Microsoft's own names appear only sometimes ("26H2" in the Arm64 display name, "22h2" in ESD file names).
    private static readonly Dictionary<int, string> ByBuild = new()
    {
        [19045] = "22H2",
        [22000] = "21H2",
        [22621] = "22H2",
        [22631] = "23H2",
        [26100] = "24H2",
        [26200] = "25H2",
        [26300] = "26H2",
    };

    /// <summary>"24H2 (26100.4349)", or whichever of the two parts is known.</summary>
    public static string? Describe(string? release, string? build)
    {
        release ??= build is null ? null : NameOfBuild(build);

        return (release, build) switch
        {
            (not null, not null) => $"{release.ToUpperInvariant()} ({build})",
            (not null, null) => release.ToUpperInvariant(),
            _ => build,
        };
    }

    /// <summary>Finds "26H2" or "22h2" in a product or file name, ignoring look-alikes inside longer words.</summary>
    public static string? FindRelease(string text)
    {
        var match = ReleaseToken().Match(text);
        return match.Success ? match.Value.ToUpperInvariant() : null;
    }

    /// <summary>Finds "26300.9457" in a display name such as "Windows 11 Client - Build 26300.9457".</summary>
    public static string? FindBuild(string text)
    {
        var match = BuildToken().Match(text);
        return match.Success ? match.Value : null;
    }

    private static string? NameOfBuild(string build) =>
        int.TryParse(build.Split('.')[0], out var number) && ByBuild.TryGetValue(number, out var name) ? name : null;

    [GeneratedRegex(@"(?<![A-Za-z0-9])\d{2}H[12](?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseToken();

    [GeneratedRegex(@"(?<![0-9.])\d{5}\.\d+(?![0-9.])")]
    private static partial Regex BuildToken();
}
