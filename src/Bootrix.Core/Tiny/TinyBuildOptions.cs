// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Tiny;

public sealed record TinyBuildOptions
{
    /// <summary>Root of the original media: a mounted ISO or an extracted folder.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Scratch space; needs roughly three times the size of the source install image.</summary>
    public required string WorkDirectory { get; init; }

    public required int ImageIndex { get; init; }

    public required string ProfileId { get; init; }

    /// <summary>Option groups of the profile that are switched off, e.g. "edge" to keep the browser.</summary>
    public IReadOnlySet<string> DisabledGroups { get; init; } = new HashSet<string>();

    /// <summary>Write an ISO here; when null the finished media folder is left for the USB writer.</summary>
    public string? IsoPath { get; init; }

    public string VolumeLabel { get; init; } = "TINY";

    /// <summary>Compression of the final install image. A USB stick formatted as FAT32 needs <see cref="InstallImageCompression.Maximum"/> so the file can be split.</summary>
    public InstallImageCompression Compression { get; init; } = InstallImageCompression.Maximum;

    /// <summary>Let Setup accept unsupported hardware (changes boot.wim).</summary>
    public bool BypassHardwareChecks { get; init; } = true;

    /// <summary>Answer file placed at the root of the media; null leaves Setup interactive.</summary>
    public UnattendOptions? Unattend { get; init; }

    /// <summary>Required for profiles that make the image impossible to service afterwards.</summary>
    public bool AcknowledgeNoServicing { get; init; }

    public bool KeepWorkDirectory { get; init; }
}

public sealed record TinyBuildResult(string MediaDirectory, string? IsoPath);
