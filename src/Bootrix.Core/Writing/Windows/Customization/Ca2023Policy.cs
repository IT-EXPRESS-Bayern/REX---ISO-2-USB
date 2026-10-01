// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Writing.Windows.Customization;

public enum Ca2023Mode
{
    /// <summary>The medium keeps the boot files of the image.</summary>
    Off,

    /// <summary>Swap when the image can provide the 2023 files and the result checks out; otherwise leave the medium as it is.</summary>
    BestEffort,

    /// <summary>The user asked for it: failing to swap is an error.</summary>
    Required,
}

/// <summary>Decides whether a job swaps the boot manager to the one signed by the Windows UEFI CA 2023.</summary>
public static class Ca2023Policy
{
    /// <summary>
    /// First build of Windows 11 (25H2) whose boot.wim carries the 2023-signed boot manager. Earlier images,
    /// including every 24H2 ISO, have no such files; Rufus documents the same limit.
    /// </summary>
    public const int FirstBuildWithBootFiles = 26200;

    public static Ca2023Mode Decide(BootCertificate certificate, int build) => certificate switch
    {
        BootCertificate.Windows2023 => Ca2023Mode.Required,

        // "Auto" has no knowledge of the target machine. It takes the new files only where the image is known to have them.
        BootCertificate.Auto when build >= FirstBuildWithBootFiles => Ca2023Mode.BestEffort,
        _ => Ca2023Mode.Off,
    };
}
