// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

public sealed record WindowsCopyOptions
{
    /// <summary>FAT32 stores sizes in 32 bits; a file may be one byte short of 4 GiB.</summary>
    public const long Fat32MaxFileBytes = (4L << 30) - 1;

    /// <summary>
    /// The size Microsoft documents for DISM /Split-Image on FAT32 media (3800 MiB). Parts end at resource
    /// boundaries, so they come out somewhat smaller, and a single large resource can make one a bit larger;
    /// the margin to 4 GiB covers both.
    /// </summary>
    public const long DefaultSplitPartBytes = 3800L * 1024 * 1024;

    /// <summary>The largest file the target file system holds; null when it has no limit that matters (NTFS, exFAT).</summary>
    public long? MaxFileBytes { get; init; }

    /// <summary>Name of the file system for the error message when a file does not fit.</summary>
    public string FileSystemName { get; init; } = "FAT32";

    /// <summary>An install.wim or install.esd over the limit is written as install.swm, install2.swm, ... instead of failing.</summary>
    public bool SplitInstallImage { get; init; }

    public long SplitPartBytes { get; init; } = DefaultSplitPartBytes;
}
