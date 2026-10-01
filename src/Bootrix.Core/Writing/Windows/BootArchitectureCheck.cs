// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Planning;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// Compares what the image can start with what the plan promises. The planner knows about missing boot files;
/// what it cannot see is that the loader in the image is for a processor other than the one the stick is
/// meant for. The findings go to the job log, so that a stick that will not start on the customer's PC has a
/// trace of why.
/// </summary>
public static class BootArchitectureCheck
{
    public static IReadOnlyList<string> Findings(ImageInspection inspection, MediaPlan plan)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(plan);

        var findings = new List<string>();
        var uefi = plan.BootMethod.HasFlag(BootMethod.UefiNative) || plan.BootMethod.HasFlag(BootMethod.UefiNtfs);
        var arch = inspection.Profile.Arch;
        var loaders = inspection.EfiArchitectures;

        if (uefi && loaders.Count > 0)
        {
            if (loaders.All(loader => loader == WindowsArch.X86))
            {
                findings.Add("The image has only a 32-bit UEFI loader (bootia32.efi). Almost all PCs have 64-bit UEFI firmware and will not start it.");
            }
            else if (loaders.All(loader => loader == WindowsArch.Arm64))
            {
                findings.Add("The image has only an ARM64 UEFI loader. PCs with x86 processors will not start it.");
            }
            else if (arch != WindowsArch.Unknown && !loaders.Contains(arch))
            {
                findings.Add($"The image holds Windows for {arch}, but its UEFI loaders are for {string.Join(", ", loaders)}.");
            }
        }

        if (arch == WindowsArch.Arm64 && plan.BootMethod.HasFlag(BootMethod.WindowsBootmgrBios))
        {
            findings.Add("ARM64 computers have no BIOS; the boot sector for BIOS start is written, but nothing can use it.");
        }

        if (uefi && inspection.Windows?.NeedsEfiLoaderExtraction == true)
        {
            findings.Add("The image has bootmgr.efi but no EFI\\BOOT loader; the medium will not start in UEFI mode without taking the loader from install.wim.");
        }

        return findings;
    }
}
