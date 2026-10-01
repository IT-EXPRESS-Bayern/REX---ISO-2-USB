// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Policy;

namespace Bootrix.Core.Planning;

/// <summary>Distribution families whose behaviour changes the layout. The full per-family policy arrives with the Linux policy data; these are the cases the planner already depends on.</summary>
internal static class LinuxFamilies
{
    // Ubuntu flavours moved their BIOS boot from isolinux to GRUB.
    private static readonly HashSet<string> GrubForBios = new(StringComparer.OrdinalIgnoreCase)
    {
        "ubuntu", "kubuntu", "xubuntu", "lubuntu", "ubuntu-mate", "ubuntu-budgie", "ubuntu-studio", "edubuntu",
    };

    // casper finds its live medium on vfat, ntfs and the ext family, but not exFAT, and calls the persistence partition "writable".
    private static readonly HashSet<string> Casper = new(StringComparer.OrdinalIgnoreCase)
    {
        "ubuntu", "kubuntu", "xubuntu", "lubuntu", "ubuntu-mate", "ubuntu-budgie", "ubuntu-studio", "edubuntu",
        "mint", "zorin", "elementary", "neon",
    };

    public static bool UsesGrubForBios(string? family) => family is not null && GrubForBios.Contains(family);

    // The names the policy data uses (linuxmint, popos, ...) are covered by its "casper" flag.
    public static bool UsesCasper(string? family) =>
        family is not null && (Casper.Contains(family) || ImagePolicy.Default.TraitsOf(family).Casper);

    /// <summary>Casper looks for "writable"; Debian's live-boot, which most other live systems use, looks for "persistence".</summary>
    public static string PersistenceLabel(string? family) => UsesCasper(family) ? "writable" : "persistence";
}
