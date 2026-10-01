// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Rescue;

/// <summary>The rescue catalog as one unit. Documents come out of the reader, which applies the schema checks, rather than being assembled by hand.</summary>
public sealed record RescueCatalogDocument
{
    public const int CurrentSchema = 1;

    /// <summary>Rises with every publication; the newer of the embedded and the downloaded catalog wins.</summary>
    public required long Version { get; init; }

    public required DateOnly Issued { get; init; }

    /// <summary>After this day the data counts as stale: still usable, but the user is told that links may have moved on.</summary>
    public required DateOnly Expires { get; init; }

    public required IReadOnlyList<RescueEntry> Entries { get; init; }
}
