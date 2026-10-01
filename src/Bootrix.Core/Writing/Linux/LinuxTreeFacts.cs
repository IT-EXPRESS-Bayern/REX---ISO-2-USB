// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Boot.Syslinux;

namespace Bootrix.Core.Writing.Linux;

/// <summary>
/// What the file tree of a Linux image offers to a boot loader: the Syslinux configuration and which Syslinux built it,
/// GRUB's configuration, the EFI loaders and the checksum list. The rules follow Rufus' scan of ISO images.
/// </summary>
public sealed partial record LinuxTreeFacts
{
    private static readonly string[] SyslinuxConfigNames = ["isolinux.cfg", "syslinux.cfg", "extlinux.conf"];

    /// <summary>The configuration Syslinux would start with: of isolinux.cfg, syslinux.cfg and extlinux.conf the one with the shortest path.</summary>
    public string? SyslinuxConfig { get; init; }

    /// <summary>The directory of <see cref="SyslinuxConfig"/>, without slashes at either end; empty for the root.</summary>
    public string SyslinuxDirectory => SyslinuxConfig is { } config && config.LastIndexOf('/') is var slash and >= 0 ? config[..slash] : "";

    public string? IsolinuxBinary { get; init; }

    /// <summary>The version in the banner of isolinux.bin; null when the image has no isolinux.bin or it carries no banner.</summary>
    public SyslinuxVersion? IsolinuxVersion { get; init; }

    /// <summary>The ldlinux.c32 that belongs to <see cref="SyslinuxConfig"/>, in its directory or in the root.</summary>
    public string? LdlinuxModule { get; init; }

    /// <summary>boot/grub/grub.cfg: the only place the GRUB that Bootrix installs looks for its menu.</summary>
    public bool HasGrubConfig { get; init; }

    /// <summary>A GRUB menu in boot/grub2, which the installed GRUB does not read by itself.</summary>
    public string? Grub2Config { get; init; }

    public bool HasGrubBiosModules { get; init; }

    /// <summary>EFI loaders (boot*, grub*, mm*, shim*) anywhere below efi/, with their lengths.</summary>
    public IReadOnlyList<IsoFile> EfiLoaders { get; init; } = [];

    /// <summary>A bootx64.efi so short that it can only be a dangling Rock Ridge symbolic link (Linux Mint 21 ships these).</summary>
    public bool HasBrokenFallbackLoader { get; init; }

    public string? ChecksumList { get; init; }

    public static LinuxTreeFacts Scan(IsoContent iso)
    {
        ArgumentNullException.ThrowIfNull(iso);

        var config = SyslinuxConfigNames
            .SelectMany(name => iso.Find("**/" + name).Concat(iso.Find(name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var binary = iso.Find("**/isolinux.bin").Concat(iso.Find("isolinux.bin")).Concat(iso.Find("**/boot.bin"))
            .OrderBy(path => ShareDirectory(path, config) ? 0 : 1)
            .ThenBy(path => path.Length)
            .FirstOrDefault();

        var loaders = iso.Files.Where(file => EfiLoader().IsMatch(file.Path)).ToList();
        return new LinuxTreeFacts
        {
            SyslinuxConfig = config,
            IsolinuxBinary = binary,
            IsolinuxVersion = binary is null ? null : ReadVersion(iso, binary),
            LdlinuxModule = FindLdlinux(iso, config),
            HasGrubConfig = iso.Contains("boot/grub/grub.cfg"),
            Grub2Config = iso.Contains("boot/grub2/grub.cfg") ? "boot/grub2/grub.cfg" : null,
            HasGrubBiosModules = iso.ContainsDirectory("boot/grub/i386-pc") || iso.ContainsDirectory("boot/grub2/i386-pc"),
            EfiLoaders = loaders,
            HasBrokenFallbackLoader = loaders.Any(file => FallbackLoader().IsMatch(file.Path) && file.Length < 256),
            ChecksumList = iso.Contains("md5sum.txt") ? "md5sum.txt" : iso.Contains("MD5SUMS") ? "MD5SUMS" : null,
        };
    }

    private static SyslinuxVersion? ReadVersion(IsoContent iso, string binary)
    {
        using var stream = iso.OpenFile(binary);
        var buffer = new byte[(int)Math.Min(stream.Length, 256 * 1024)];
        stream.ReadExactly(buffer);
        return SyslinuxVersion.FromBinary(buffer);
    }

    private static string? FindLdlinux(IsoContent iso, string? config)
    {
        if (config is null)
        {
            return null;
        }

        var directory = config.LastIndexOf('/') is var slash and >= 0 ? config[..slash] : "";
        var candidates = new[] { directory.Length == 0 ? "ldlinux.c32" : directory + "/ldlinux.c32", "ldlinux.c32", "boot/syslinux/ldlinux.c32", "syslinux/ldlinux.c32" };
        return candidates.FirstOrDefault(iso.Contains);
    }

    private static bool ShareDirectory(string path, string? config) =>
        config is not null && string.Equals(path[..Math.Max(0, path.LastIndexOf('/'))], config[..Math.Max(0, config.LastIndexOf('/'))], StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^efi/(?:.+/)?(?:boot|grub|mm|shim)(?:ia32|x64|arm|aa64|ia64|riscv64|loongarch64|ebc)?\.efi$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EfiLoader();

    [GeneratedRegex(@"^efi/boot/boot(?:ia32|x64|arm|aa64)\.efi$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FallbackLoader();
}
