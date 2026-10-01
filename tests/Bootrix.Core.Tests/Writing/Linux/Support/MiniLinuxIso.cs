// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Writing.Linux.Support;

public enum MiniFlavor
{
    /// <summary>A Debian live image: isolinux and GRUB menus, "boot=live".</summary>
    DebianLive,

    /// <summary>An Ubuntu image: casper, GRUB menu, isolinux menu.</summary>
    Ubuntu,
}

internal sealed record MiniIsoOptions
{
    public MiniFlavor Flavor { get; init; } = MiniFlavor.DebianLive;

    public string Label { get; init; } = "MINI_LIVE";

    public bool Isolinux { get; init; } = true;

    public bool Grub { get; init; } = true;

    /// <summary>Where the GRUB menu lives: boot/grub, or boot/grub2 as on openSUSE and Fedora.</summary>
    public string GrubDirectory { get; init; } = "boot/grub";

    /// <summary>The image carries GRUB's BIOS modules, which is what tells the inspector that it can boot in BIOS mode without Syslinux.</summary>
    public bool GrubBiosModules { get; init; }

    /// <summary>The image carries an ldlinux.c32; Syslinux 4 images do not.</summary>
    public bool IncludeLdlinux { get; init; } = true;

    /// <summary>The banner of isolinux.bin, which decides which Syslinux the writer has to match.</summary>
    public string IsolinuxBanner { get; init; } = "ISOLINUX 6.04 20190206";

    /// <summary>Release whose ldlinux.c32 the image carries, as a distribution would ship its own.</summary>
    public string LdlinuxRelease { get; init; } = "6.03";

    public bool EfiLoader { get; init; } = true;
}

/// <summary>
/// A small live-Linux ISO built with xorriso: real file system, real Rock Ridge and Joliet names, menus for Syslinux
/// and GRUB, and the fake kernel from the fixtures, which reports its command line on the serial port.
/// </summary>
internal static class MiniLinuxIso
{
    private const string BootParameters = "components quiet";

    public static readonly string[] RequiredTools = ["xorriso", "mcopy", "qemu-system-x86_64"];

    public static string Build(string isoPath, string workDirectory, MiniIsoOptions options)
    {
        var tree = Path.Combine(workDirectory, "tree-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tree);
        var live = options.Flavor == MiniFlavor.DebianLive;
        var kernelDirectory = live ? "live" : "casper";
        // dracut and udev write blanks of a label as \x20 on the command line.
        var label = options.Label.Replace(" ", "\\x20", StringComparison.Ordinal);
        var kernelParameters = live
            ? $"boot=live {BootParameters} root=live:CDLABEL={label}"
            : $"boot=casper {BootParameters} root=live:CDLABEL={label}";

        Put(tree, $"{kernelDirectory}/vmlinuz", File.ReadAllBytes(Fixtures.Path("fakekernel.bin")));
        Put(tree, $"{kernelDirectory}/initrd.img", Fixtures.Initrd());
        Put(tree, live ? "live/filesystem.squashfs" : "casper/filesystem.squashfs", new byte[4096]);
        if (!live)
        {
            Put(tree, ".disk/info", Encoding.ASCII.GetBytes("Ubuntu 24.04 LTS \"Noble Numbat\" - Release amd64\n"));
        }

        if (options.Isolinux)
        {
            var banner = Encoding.ASCII.GetBytes(options.IsolinuxBanner + " Copyright (C) 1994-2015 H. Peter Anvin et al\0");
            var binary = new byte[4096];
            banner.CopyTo(binary, 128);
            Put(tree, "isolinux/isolinux.bin", binary);
            Put(tree, "isolinux/isolinux.cfg", Encoding.ASCII.GetBytes(
                "SERIAL 0 115200\nPROMPT 0\nDEFAULT live\n\nLABEL live\n  MENU LABEL Mini live\n" +
                $"  KERNEL /{kernelDirectory}/vmlinuz\n  APPEND initrd=/{kernelDirectory}/initrd.img {kernelParameters}\n"));
            if (options.IncludeLdlinux)
            {
                Put(tree, "isolinux/ldlinux.c32", SyslinuxBundle.Shipped.Single(b => b.Id == options.LdlinuxRelease).LdlinuxModule.ToArray());
            }
        }

        if (options.Grub)
        {
            Put(tree, options.GrubDirectory + "/grub.cfg", Encoding.ASCII.GetBytes(
                "serial --unit=0 --speed=115200\nterminal_input serial\nterminal_output serial\nset default=0\nset timeout=0\n\n" +
                $"menuentry \"Mini live\" {{\n\tlinux /{kernelDirectory}/vmlinuz {kernelParameters}\n\tinitrd /{kernelDirectory}/initrd.img\n}}\n"));
        }

        if (options.GrubBiosModules)
        {
            Put(tree, options.GrubDirectory + "/i386-pc/normal.mod", new byte[64]);
        }

        if (options.EfiLoader)
        {
            Put(tree, "EFI/BOOT/BOOTX64.EFI", Encoding.ASCII.GetBytes("MZ not a real EFI program"));
        }

        WriteChecksums(tree);
        var result = ExternalTools.Run("xorriso", "-as", "mkisofs", "-r", "-J", "-V", options.Label, "-o", isoPath, tree);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("xorriso failed: " + result.Combined);
        }

        return isoPath;
    }

    private static void Put(string root, string relative, byte[] content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    /// <summary>md5sum.txt of the files so far, written the way Ubuntu and Debian do.</summary>
    [SuppressMessage("Security", "CA5351", Justification = "The list format of md5sum.txt is MD5; nothing is authenticated with it.")]
    private static void WriteChecksums(string tree)
    {
        var lines = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(tree, file).Replace(Path.DirectorySeparatorChar, '/');
            lines.Append(Convert.ToHexString(MD5.HashData(File.ReadAllBytes(file))).ToLowerInvariant()).Append("  ./").Append(relative).Append('\n');
        }

        File.WriteAllText(Path.Combine(tree, "md5sum.txt"), lines.ToString());
    }
}

internal static class Fixtures
{
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Writing", "Linux", "Fixtures", name);

    /// <summary>A 4 KiB "initial ramdisk" with content that cannot be mistaken for anything else.</summary>
    public static byte[] Initrd()
    {
        var bytes = new byte[4096];
        new Random(2024).NextBytes(bytes);
        return bytes;
    }
}
