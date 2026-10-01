// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Families;

/// <summary>A condition on the text content of a small file in the image, e.g. <c>.disk/info</c> or <c>.treeinfo</c>.</summary>
internal sealed record TextProbe
{
    public string Path { get; init; } = "";

    /// <summary>Regular expression, matched case-insensitively with <c>^</c> and <c>$</c> per line.</summary>
    public string Pattern { get; init; } = "";
}

/// <summary>
/// One fingerprint of an image family. Every condition that is present must hold; rules are tried by
/// descending priority and the first match decides.
/// </summary>
internal sealed record FamilyRule
{
    public string Family { get; init; } = "";

    /// <summary>An <see cref="ImageKind"/> name, or "Linux" to derive LinuxHybrid / LinuxIsoOnly / RawDisk from the image.</summary>
    public string Kind { get; init; } = "Linux";

    public int Priority { get; init; }

    /// <summary>"iso" for optical file systems, "disk" for raw disk and floppy images; absent matches both.</summary>
    public string? Container { get; init; }

    /// <summary>Regular expression for the volume label.</summary>
    public string? Label { get; init; }

    /// <summary>Regular expression for the image's file name.</summary>
    public string? FileName { get; init; }

    /// <summary>Glob patterns that must all match something in the file tree.</summary>
    public string[] All { get; init; } = [];

    /// <summary>Glob patterns of which at least one must match.</summary>
    public string[] Any { get; init; } = [];

    /// <summary>Glob patterns of which none may match.</summary>
    public string[] None { get; init; } = [];

    public TextProbe[] Text { get; init; } = [];

    /// <summary>GPT partition names of which at least one must be present.</summary>
    public string[] PartitionNames { get; init; } = [];

    /// <summary>MBR partition types of which at least one must be present.</summary>
    public int[] MbrTypes { get; init; } = [];

    /// <summary>GPT partition type GUIDs of which at least one must be present.</summary>
    public Guid[] GptTypes { get; init; } = [];

    /// <summary>The image is exactly this many bytes long (standard floppy sizes).</summary>
    public long[] ImageSizes { get; init; } = [];
}

internal sealed record FamilyRuleSet
{
    public int Version { get; init; } = 1;

    public FamilyRule[] Rules { get; init; } = [];
}
