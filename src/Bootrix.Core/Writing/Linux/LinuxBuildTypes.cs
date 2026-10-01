// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Policy;
using Bootrix.Core.Writing.Linux.Patching;

namespace Bootrix.Core.Writing.Linux;

/// <summary>What the media builder is told about the medium it fills.</summary>
public sealed record LinuxBuildSettings
{
    public required BiosBootDecision Bios { get; init; }

    /// <summary>The medium is meant to start in UEFI mode too, so the EFI loaders have to be complete and are checked.</summary>
    public bool Uefi { get; init; }

    /// <summary>The label as stored on the medium (already shortened and upper-cased for FAT); what boot configurations have to name.</summary>
    public string? MediumLabel { get; init; }

    public string? Family { get; init; }

    public FamilyTraits Traits { get; init; } = FamilyTraits.None;

    /// <summary>A persistence partition is part of the plan and the boot configuration should switch it on.</summary>
    public bool Persistence { get; init; }
}

public enum WrittenFileSource
{
    /// <summary>Copied unchanged from the image.</summary>
    Image,

    /// <summary>The image's file with changes; <see cref="WrittenFile.Content"/> holds what was written.</summary>
    Patched,

    /// <summary>Created by the builder (redirect configuration, loader, modules).</summary>
    Generated,
}

public sealed record WrittenFile(string Path, long Length, WrittenFileSource Source, byte[]? Content = null);

public sealed record PatchedConfig(string Path, IReadOnlyList<ConfigChange> Changes);

public sealed record LinuxBuildResult(
    BiosBootDecision Bios,
    IReadOnlyList<WrittenFile> Files,
    IReadOnlyList<PatchedConfig> Patches,
    IReadOnlyList<ImageWarning> Notices,
    EfiAnalysisReport? Efi)
{
    public long Bytes => Files.Sum(file => file.Length);

    /// <summary>Whether ldlinux.sys was put on the medium and is waiting for the installer to patch it.</summary>
    public bool NeedsSyslinuxInstall => Bios.Loader == BiosLoader.Syslinux;
}

public readonly record struct LinuxCopyProgress(long BytesDone, long BytesTotal, string Path);
