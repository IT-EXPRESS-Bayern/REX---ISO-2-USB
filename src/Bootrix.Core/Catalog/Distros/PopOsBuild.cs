// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Channel">"intel" (also for AMD graphics) or "nvidia".</param>
internal sealed record PopOsBuild(string Version, string Channel, Uri Url, long Size, FileHash Sha256, string Build)
{
    /// <summary>
    /// Reads an answer of <c>api.pop-os.org/builds/{version}/{channel}</c>, the endpoint System76's download page uses.
    /// Returns null if the answer lacks a usable address, size or digest.
    /// </summary>
    public static PopOsBuild? Parse(string json)
    {
        using var document = DistroJson.Parse(json, "Pop!_OS builds API");
        var root = document.RootElement;

        var size = root.Number("size");
        if (root.String("version") is not { Length: > 0 } version
            || root.String("channel") is not { Length: > 0 } channel
            || !Uri.TryCreate(root.String("url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !FileHash.TryCreate(HashKind.Sha256, root.String("sha_sum"), out var sha256)
            || size is not > 0)
        {
            return null;
        }

        return new PopOsBuild(version, channel, url, size.Value, sha256!, root.String("build") ?? string.Empty);
    }
}
