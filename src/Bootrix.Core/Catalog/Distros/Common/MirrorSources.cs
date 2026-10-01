// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros.Common;

internal static class MirrorSources
{
    /// <summary>More sources than the downloader has connections only add probes; the best few are enough.</summary>
    public const int DefaultLimit = 8;

    /// <summary>
    /// The vendor's own address first, then mirrors that carry the same tree. <paramref name="relativePath"/> is the
    /// file's path below each mirror root; the roots end with a slash.
    /// </summary>
    public static IReadOnlyList<MirrorSource> Build(
        Uri primary,
        IEnumerable<(string Root, string? Location)> mirrors,
        string relativePath)
    {
        var sources = new List<MirrorSource> { new(primary, 1) };
        sources.AddRange(mirrors.Select(m => new MirrorSource(new Uri(new Uri(m.Root), relativePath), 2, m.Location)));
        return sources;
    }

    /// <summary>
    /// The best of a vendor's mirror list: HTTPS ones if there are any (a file named in the clear tells a network what
    /// the user installs), in the vendor's priority order, at most <paramref name="limit"/> of them.
    /// </summary>
    public static IReadOnlyList<MirrorSource> Pick(IEnumerable<MirrorSource> mirrors, int limit = DefaultLimit)
    {
        var all = mirrors.ToList();
        var secure = all.Where(m => m.Url.Scheme == Uri.UriSchemeHttps).ToList();

        return [.. (secure.Count > 0 ? secure : all).OrderBy(m => m.Priority).Take(limit)];
    }
}
