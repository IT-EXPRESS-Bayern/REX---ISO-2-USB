// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Images.Policy;

/// <summary>Boot support in one mode, for families where it cannot be derived from the image.</summary>
internal sealed record BootOverride
{
    public BootSupport? Bios { get; init; }

    public BootSupport? Uefi { get; init; }
}

internal sealed record PersistencePolicy
{
    public bool RawCopy { get; init; }

    public bool Extract { get; init; }

    /// <summary>The mode to use when persistence is wanted and both are possible.</summary>
    public WriteMode Preferred { get; init; } = WriteMode.Extract;
}

/// <summary>
/// What is known about one distribution family. Values left out are inherited from <see cref="Inherits"/>;
/// the lists of reasons, warnings and flags add up along the chain.
/// </summary>
internal sealed record FamilyPolicy
{
    public string? Inherits { get; init; }

    public string? Name { get; init; }

    /// <summary>Modes that work for the family's optical images; absent means whatever the image itself allows.</summary>
    public WriteMode[]? Modes { get; init; }

    public WriteMode? Default { get; init; }

    public PersistencePolicy? Persistence { get; init; }

    /// <summary>The boot configuration refers to the volume label, so extract mode has to rewrite it when the label changes.</summary>
    public bool? LabelPatch { get; init; }

    public BootOverride? Raw { get; init; }

    public BootOverride? Extract { get; init; }

    public string[] Reasons { get; init; } = [];

    public string[] Warnings { get; init; } = [];

    /// <summary>Behaviour switches the evaluation code knows: "casper", "liveBoot".</summary>
    public string[] Flags { get; init; } = [];
}

internal sealed record PolicyDocument
{
    public int Version { get; init; } = 1;

    public Dictionary<string, FamilyPolicy> Families { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
