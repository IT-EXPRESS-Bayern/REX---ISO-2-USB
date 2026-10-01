// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Rescue;

/// <summary>Keys of texts that catalog entries refer to; the texts themselves are in the UI resources.</summary>
public static class RescueNotices
{
    /// <summary>Required on every offline password tool: use only on devices and accounts one owns or is authorised to service.</summary>
    public const string AuthorizedUseOnly = "Notice.AuthorizedUseOnly";
}

public sealed record RescueEntry
{
    /// <summary>Stable id inside the rescue catalog, e.g. "systemrescue".</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required LocalizedText Description { get; init; }

    public required RescueCategory Category { get; init; }

    /// <summary>An SPDX expression, "proprietary-freeware" or "proprietary".</summary>
    public required string License { get; init; }

    /// <summary>The vendor's own page.</summary>
    public required Uri Homepage { get; init; }

    /// <summary>Programs on the medium that people search for, such as "TestDisk".</summary>
    public IReadOnlyList<string> IncludedTools { get; init; } = [];

    /// <summary>Keys of legal or safety notices that must be shown before the download (<c>Notice.*</c>).</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];

    /// <summary>Keys of practical hints (<c>Hint.*</c>).</summary>
    public IReadOnlyList<string> Hints { get; init; } = [];

    public required IReadOnlyList<RescueVariant> Variants { get; init; }
}
