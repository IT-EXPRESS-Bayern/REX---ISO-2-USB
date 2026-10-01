// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros.Common;

internal static class MirrorSources
{
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
}
