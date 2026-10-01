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

        // "Auto" knows nothing about the machine that will boot the stick. A boot manager signed only by the 2023 CA
        // does not start on firmware that has not received that certificate yet, so the default stays with the
        // 2011-signed files that every current machine accepts. The target-PC check suggests the 2023 files where the
        // firmware needs them, and the user can ask for them explicitly.
        _ => Ca2023Mode.Off,
    };
}
