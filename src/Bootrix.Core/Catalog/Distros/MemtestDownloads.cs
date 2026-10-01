// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Kind">"x86_64", "x86_64-grub" or "i586".</param>
/// <param name="ImageInArchive">Name of the ISO inside the zip.</param>
internal sealed record MemtestDownload(string Version, string Kind, string FileName, string Architecture, string ImageInArchive);

/// <summary>The ISO downloads that memtest.org lists for its current release.</summary>
internal static partial class MemtestDownloads
{
    /// <summary>
    /// The three ISO builds linked on the front page. The LoongArch build exists on the server but is not linked and
    /// not for PCs. The ISO is delivered inside a zip that holds exactly one file, named differently for the GRUB build.
    /// </summary>
    public static IReadOnlyList<MemtestDownload> Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        return [.. DownloadLink().Matches(html)
            .Select(m => ToDownload(m))
            .DistinctBy(d => d.FileName)
            .OrderBy(d => Rank(d.Kind))];
    }

    public static Uri DirectoryFor(string version) => new($"https://memtest.org/download/v{version}/");

    private static MemtestDownload ToDownload(Match match)
    {
        var grub = match.Groups["grub"].Success;
        var architecture = match.Groups["arch"].Value == "i586" ? Architectures.I386 : Architectures.X64;
        var kind = match.Groups["arch"].Value + (grub ? "-grub" : string.Empty);

        return new MemtestDownload(
            match.Groups["version"].Value,
            kind,
            Path.GetFileName(match.Groups["path"].Value),
            architecture,
            grub ? "grub-memtest.iso" : "memtest.iso");
    }

    private static int Rank(string kind) => kind switch
    {
        "x86_64" => 0,
        "x86_64-grub" => 1,
        _ => 2,
    };

    [GeneratedRegex(@"href=""(?<path>/download/v(?<version>\d+\.\d+)/mt86plus_\k<version>_(?<arch>x86_64|i586)(?<grub>\.grub)?\.iso\.zip)""")]
    private static partial Regex DownloadLink();
}
