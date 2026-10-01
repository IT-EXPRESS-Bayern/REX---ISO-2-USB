// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>
/// A partition independent of the table format it will be stored in. It carries the identifiers
/// of both schemes; the layout's scheme decides which of them is written.
/// </summary>
public sealed record PartitionEntry
{
    public required long StartLba { get; init; }

    public required long SectorCount { get; init; }

    /// <summary>The MBR type byte; also the type of the mirror entry in a hybrid MBR.</summary>
    public byte MbrType { get; init; }

    public Guid GptType { get; init; }

    /// <summary>Null lets the GPT builder generate one.</summary>
    public Guid? UniqueId { get; init; }

    /// <summary>The GPT partition name (up to 36 characters); MBR has no equivalent.</summary>
    public string Name { get; init; } = "";

    /// <summary>The MBR boot flag.</summary>
    public bool Active { get; init; }

    public GptAttributes Attributes { get; init; }

    /// <summary>Last sector of the partition, inclusive.</summary>
    public long EndLba => StartLba + SectorCount - 1;
}
