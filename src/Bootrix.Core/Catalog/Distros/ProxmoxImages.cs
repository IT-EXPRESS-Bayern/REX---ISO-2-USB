// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Product">File name prefix: "proxmox-ve", "proxmox-backup-server", "proxmox-mail-gateway", "proxmox-datacenter-manager".</param>
/// <param name="Version">Release plus ISO revision as Proxmox writes it, "9.2-1".</param>
internal sealed record ProxmoxImage(string FileName, string Product, string Version, NumericVersion Release, string Architecture);

internal static partial class ProxmoxImages
{
    /// <summary>
    /// The ISOs of the four Proxmox products in the shared <c>SHA256SUMS</c>, newest release first. A release comes
    /// as x86-64 and, since Proxmox VE 9.2, as <c>-arm64</c>.
    /// </summary>
    public static IReadOnlyList<ProxmoxImage> Parse(ChecksumFile sums)
    {
        ArgumentNullException.ThrowIfNull(sums);

        return [.. sums.Entries
            .Where(e => e.FileName is not null)
            .Select(e => (Name: e.FileName!, Match: ImageName().Match(e.FileName!)))
            .Where(x => x.Match.Success && NumericVersion.TryParse(x.Match.Groups["release"].Value, out _))
            .Select(x => new ProxmoxImage(
                x.Name,
                x.Match.Groups["product"].Value,
                $"{x.Match.Groups["release"].Value}-{x.Match.Groups["revision"].Value}",
                NumericVersion.Parse(x.Match.Groups["release"].Value),
                x.Match.Groups["arm"].Success ? Architectures.Arm64 : Architectures.X64))
            .DistinctBy(i => i.FileName)
            .OrderByDescending(i => i.Release)
            .ThenByDescending(i => i.Version, StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"^(?<product>proxmox-ve|proxmox-backup-server|proxmox-mail-gateway|proxmox-datacenter-manager)_(?<release>\d+\.\d+)-(?<revision>\d+)(?<arm>-arm64)?\.iso$")]
    private static partial Regex ImageName();
}
