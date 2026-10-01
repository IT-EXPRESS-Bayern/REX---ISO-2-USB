// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Linux.Patching;

/// <summary>How a live system finds a persistence store, which decides the kernel parameter that turns it on.</summary>
public enum PersistenceStyle
{
    None,

    /// <summary>Ubuntu's casper: the parameter "persistent".</summary>
    Casper,

    /// <summary>Debian's live-boot: the parameter "persistence".</summary>
    LiveBoot,
}

public sealed record ConfigPatchOptions
{
    /// <summary>The label of the ISO volume as the boot configuration spells it.</summary>
    public string? OldLabel { get; init; }

    /// <summary>The label of the medium. Nothing is rewritten when it is empty or equal to <see cref="OldLabel"/>.</summary>
    public string? NewLabel { get; init; }

    public PersistenceStyle Persistence { get; init; }

    /// <summary>VMware ESXi: the installer finds its files on the first partition only when the boot line says "-p 1".</summary>
    public bool EsxiFirstPartition { get; init; }
}

public enum ConfigChangeKind
{
    Label,
    PersistenceParameter,
    RemovedParameter,
    EsxiPartition,
}

/// <param name="Line">One-based line number.</param>
public sealed record ConfigChange(int Line, ConfigChangeKind Kind, string Before, string After);

public sealed record ConfigPatchResult(string Text, IReadOnlyList<ConfigChange> Changes)
{
    public bool Changed => Changes.Count > 0;
}
