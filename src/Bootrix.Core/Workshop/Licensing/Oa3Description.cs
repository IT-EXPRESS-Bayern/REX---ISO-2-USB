// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Workshop.Licensing;

/// <summary>
/// The text of SoftwareLicensingService.OA3xOriginalProductKeyDescription, typically "[4.0] Professional OEM:DM". The property
/// is not part of Microsoft's published WMI class, so nothing about its format is guaranteed and every part is optional.
/// </summary>
public sealed partial record Oa3Description(string Raw, string? EditionId, string? Channel, string? Subtype)
{
    private static readonly string[] Channels = ["OEM", "Retail", "Volume", "Eval"];

    public static Oa3Description? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var raw = text.Trim();
        var rest = VersionPrefix().Replace(raw, "").Trim();
        var tokens = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        string? channel = null;
        string? subtype = null;
        if (tokens.Length > 0 && TryChannel(tokens[^1], out channel, out subtype))
        {
            tokens = tokens[..^1];
        }

        // Look through all remaining tokens so "Windows 10 Professional" and "Professional" both resolve.
        var edition = tokens.Select(WindowsEditions.Canonical).FirstOrDefault(e => e is not null);
        return new Oa3Description(raw, edition, channel, subtype);
    }

    private static bool TryChannel(string token, out string? channel, out string? subtype)
    {
        var parts = token.Split(':', 2);
        var match = Channels.FirstOrDefault(c => c.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        channel = match;
        subtype = match is not null && parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        return match is not null;
    }

    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)+")]
    private static partial Regex VersionPrefix();
}
