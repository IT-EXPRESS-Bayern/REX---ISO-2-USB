// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Tests.Images.Support;

public enum BootEmulation
{
    None,
    Floppy,
    HardDisk,
}

public sealed record BootImageSpec(string Path, bool Efi = false, BootEmulation Emulation = BootEmulation.None, int LoadSize = 4);

public sealed class IsoOptions
{
    public string Label { get; init; } = "TESTIMG";

    public bool RockRidge { get; init; } = true;

    public bool Joliet { get; init; } = true;

    public int IsoLevel { get; init; } = 3;

    public bool Hybrid { get; init; }

    /// <summary>Adds a GPT with the EFI image as a partition (-isohybrid-gpt-basdat); needs <see cref="Hybrid"/>.</summary>
    public bool Gpt { get; init; }

    public IReadOnlyList<BootImageSpec> BootImages { get; init; } = [];
}

/// <summary>Builds ISO images with xorriso from a dictionary of files, so every family fingerprint is tested against a real image.</summary>
public static class IsoBuilder
{
    /// <summary>A boot image stand-in: the isolinux hybrid signature at offset 0x40 makes xorriso accept it for isohybrid MBRs.</summary>
    public static byte[] FakeBootLoader()
    {
        var data = new byte[2048];
        new Random(11).NextBytes(data);
        data[0x40] = 0xFB;
        data[0x41] = 0xC0;
        data[0x42] = 0x78;
        data[0x43] = 0x70;
        return data;
    }

    /// <summary>A FAT12 volume of the given size holding the files; used as EFI system partition image or as floppy.</summary>
    public static byte[] FatImage(TestDirectory dir, string name, int kib, IReadOnlyDictionary<string, string> files, string label = "BOOTIMG")
    {
        var path = dir.File(name);
        File.WriteAllBytes(path, new byte[kib * 1024]);
        var fatType = kib < 16384 ? "12" : kib <= 65536 ? "16" : "32";
        ExternalTool.Run("mkfs.vfat", ["-n", label, "-F", fatType, path], null, null, null);
        foreach (var (file, content) in files)
        {
            var source = dir.File("fat-src-" + Path.GetFileName(file));
            File.WriteAllText(source, content);
            var directories = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? string.Empty;
            var current = string.Empty;
            foreach (var part in directories.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current += "/" + part;
                ExternalTool.RunUnchecked("mmd", ["-i", path, "::" + current]);
            }

            ExternalTool.Run("mcopy", ["-i", path, source, "::/" + file.Replace('\\', '/')], null, null, null);
        }

        return File.ReadAllBytes(path);
    }

    public static string Build(TestDirectory dir, string name, IReadOnlyDictionary<string, byte[]> files, IsoOptions? options = null)
    {
        options ??= new IsoOptions();
        var tree = Path.Combine(dir.Path, "tree-" + name);
        Directory.CreateDirectory(tree);
        foreach (var (relative, content) in files)
        {
            var target = Path.Combine(tree, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }

        var iso = dir.File(name + ".iso");
        var arguments = new List<string> { "-as", "mkisofs", "-quiet", "-V", options.Label, "-o", iso };
        if (options.RockRidge)
        {
            arguments.Add("-r");
        }

        if (options.Joliet)
        {
            arguments.Add("-J");
        }

        arguments.AddRange(["-iso-level", options.IsoLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        var first = true;
        foreach (var boot in options.BootImages)
        {
            if (!first)
            {
                arguments.Add("-eltorito-alt-boot");
            }

            arguments.Add(boot.Efi ? "-e" : "-b");
            arguments.Add(boot.Path);
            if (boot.Emulation == BootEmulation.None)
            {
                arguments.Add("-no-emul-boot");
                arguments.AddRange(["-boot-load-size", boot.LoadSize.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }
            else if (boot.Emulation == BootEmulation.HardDisk)
            {
                arguments.Add("-hard-disk-boot");
            }

            if (first && !boot.Efi && boot.Emulation == BootEmulation.None)
            {
                arguments.AddRange(["-c", "boot.cat", "-boot-info-table"]);
            }

            first = false;
        }

        if (options.Hybrid)
        {
            var mbr = dir.File("isohdpfx.bin");
            File.WriteAllBytes(mbr, HybridMbr());
            arguments.AddRange(["-isohybrid-mbr", mbr]);
            if (options.Gpt)
            {
                arguments.Add("-isohybrid-gpt-basdat");
            }
        }

        arguments.Add(tree);
        ExternalTool.Run("xorriso", arguments, dir.Path, null, null);
        return iso;
    }

    /// <summary>432 bytes of boot code in the style of Syslinux' isohdpfx.bin: a few instructions, the rest zero.</summary>
    private static byte[] HybridMbr()
    {
        var code = new byte[432];
        Encoding.ASCII.GetBytes("fake isohybrid boot code").CopyTo(code, 8);
        code[0] = 0xFA;
        code[1] = 0xEB;
        code[2] = 0xFE;
        return code;
    }
}
