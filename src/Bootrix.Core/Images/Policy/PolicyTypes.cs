// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Images.Policy;

/// <summary>What the policy recommends for writing an image.</summary>
public enum PolicyMode
{
    /// <summary>Write the image byte for byte (DD).</summary>
    RawCopy,

    /// <summary>Extract the files onto a freshly prepared partition (ISO mode).</summary>
    Extract,

    /// <summary>Both are reasonable and the person has to choose.</summary>
    Ask,
}

public enum BootSupport
{
    No,

    /// <summary>Depends on the firmware or on details of the image that cannot be seen from outside.</summary>
    Maybe,

    Yes,
}

[Flags]
public enum PersistenceSupport
{
    None = 0,

    /// <summary>A persistence partition can be added behind the raw copy.</summary>
    RawCopyPartition = 1,

    /// <summary>The boot configuration can be patched in extract mode.</summary>
    ExtractMode = 2,
}

/// <summary>What is known about the intended target and the person's wishes when the policy is asked.</summary>
public sealed record PolicyRequest
{
    public bool WantsPersistence { get; init; }

    /// <summary>Logical sector size of the target disk; 4096 for 4Kn drives and some USB-SATA bridges.</summary>
    public int TargetSectorSize { get; init; } = 512;

    /// <summary>The file system extract mode will use; <see cref="FileSystemKind.Auto"/> while still open.</summary>
    public FileSystemKind TargetFileSystem { get; init; } = FileSystemKind.Auto;

    public long? TargetSizeBytes { get; init; }

    /// <summary>The mode the person chose by hand; used when the image allows it.</summary>
    public WriteMode PreferredMode { get; init; } = WriteMode.Auto;
}

public sealed record ImageDecision
{
    public required PolicyMode Mode { get; init; }

    /// <summary>The modes that can work for this image at all; <see cref="WriteMode.Auto"/> is never listed.</summary>
    public required IReadOnlyList<WriteMode> AllowedModes { get; init; }

    /// <summary>Only one mode can work, so the choice is not offered.</summary>
    public bool IsForced => AllowedModes.Count == 1;

    /// <summary>The mode as the job specification names it; <see cref="PolicyMode.Ask"/> stays <see cref="WriteMode.Auto"/>.</summary>
    public WriteMode WriteMode => Mode switch
    {
        PolicyMode.RawCopy => WriteMode.RawCopy,
        PolicyMode.Extract => WriteMode.Extract,
        _ => WriteMode.Auto,
    };

    /// <summary>Resource keys that explain the decision ("Why?" in the UI).</summary>
    public required IReadOnlyList<string> ReasonKeys { get; init; }

    public required IReadOnlyList<ImageWarning> Warnings { get; init; }

    /// <summary>How persistence could be provided for this image, independent of the chosen mode.</summary>
    public PersistenceSupport Persistence { get; init; }

    /// <summary>Persistence is possible in the recommended mode.</summary>
    public bool PersistencePossible { get; init; }

    /// <summary>The volume label of the image appears in boot configuration files that have to be rewritten for the new label.</summary>
    public bool LabelPatchRequired { get; init; }

    public BootSupport BiosBoot { get; init; }

    public BootSupport UefiBoot { get; init; }
}
