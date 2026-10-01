// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Images;

/// <summary>
/// Looks for bootloaders in the file trees of an image. The marker list follows what Rufus' ISO scan keys
/// on (syslinux, GRUB for BIOS, Windows boot manager, ReactOS, KolibriOS) and the EFI loader naming of
/// the UEFI specification (BOOT{IA32,X64,ARM,AA64}.EFI).
/// </summary>
internal static partial class BootFileProbe
{
    private static readonly string[] BiosMarkers =
    [
        "**/isolinux.bin", "**/isolinux.cfg", "**/syslinux.cfg", "**/extlinux.conf", "**/ldlinux.sys", "**/ldlinux.c32",
        "**/i386-pc", "**/mboot.c32", "bootmgr", "boot/bcd", "ntldr", "*/setupldr.bin", "*/ntldr", "grldr",
        "kolibri.img", "**/freeldr.sys", "**/setupldr.sys",
    ];

    public static bool HasBiosBootFiles(IEnumerable<ImageFileIndex> trees) =>
        trees.Any(tree => BiosMarkers.Any(tree.Matches));

    /// <summary>EFI loaders anywhere below <c>efi/</c>; Fedora and Bazzite keep theirs outside of <c>efi/boot</c>.</summary>
    public static IEnumerable<string> EfiLoaders(IEnumerable<ImageFileIndex> trees) =>
        trees.SelectMany(tree => tree.FindFiles("efi/**/*.efi")).Where(path => LoaderName().IsMatch(Path.GetFileName(path)));

    public static bool HasEfiBootFiles(IEnumerable<ImageFileIndex> trees) => EfiLoaders(trees).Any();

    public static IReadOnlyList<WindowsArch> EfiArchitectures(IEnumerable<ImageFileIndex> trees) =>
    [
        .. EfiLoaders(trees)
            .Select(path => ArchitectureOf(Path.GetFileName(path)))
            .Where(arch => arch != WindowsArch.Unknown)
            .Distinct()
            .Order(),
    ];

    private static WindowsArch ArchitectureOf(string fileName)
    {
        var match = LoaderName().Match(fileName);
        return match.Groups["arch"].Value.ToLowerInvariant() switch
        {
            "ia32" => WindowsArch.X86,
            "x64" => WindowsArch.X64,
            "aa64" => WindowsArch.Arm64,
            "arm" => WindowsArch.Arm,
            _ => WindowsArch.Unknown,
        };
    }

    [GeneratedRegex(@"^(?:boot|grub|mm|shim)(?<arch>ia32|x64|arm|aa64|ia64|riscv64|loongarch64|ebc)?\.efi$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LoaderName();
}
