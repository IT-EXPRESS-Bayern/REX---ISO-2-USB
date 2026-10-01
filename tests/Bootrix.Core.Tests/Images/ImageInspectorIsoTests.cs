// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Iso;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images;

public sealed class ImageInspectorIsoTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static HashSet<string> IsoinfoPaths(string iso, string extension)
    {
        var output = ReferenceTool.Run("isoinfo", "-f", extension, "-i", iso).StandardOutput;
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(ImageFileIndex.Normalize).Where(path => path.Length > 0)];
    }

    private static HashSet<string> IndexPaths(string iso, bool joliet, bool udf)
    {
        using var stream = File.OpenRead(iso);
        using var fileSystem = ImageFileSystem.OpenIso(stream, joliet, udf, 100_000, CancellationToken.None)!;
        return [.. fileSystem.Index.Files.Select(file => file.Path).Concat(fileSystem.Index.Directories)];
    }

    private Dictionary<string, byte[]> SampleTree() => new()
    {
        ["Boot/Grub/grub.cfg"] = Bytes("menuentry x"),
        ["a-very-long-file-name-with-many-characters.txt"] = Bytes("long"),
        ["Mixed Case/Dir With Spaces/File.TXT"] = Bytes("spaces"),
        ["deep/er/and/deeper/file.bin"] = new byte[3000],
        ["empty.txt"] = [],
    };

    [ToolTheory("xorriso", "isoinfo")]
    [InlineData(true, true, 3, "-R", "RockRidge")]
    [InlineData(false, true, 3, "-J", "Joliet")]
    [InlineData(true, false, 3, "-R", "RockRidgeOnly")]
    public void FileTree_MatchesIsoinfoForTheExtensionInUse(bool rockRidge, bool joliet, int level, string extension, string name)
    {
        var iso = IsoBuilder.Build(_dir, name, SampleTree(), new IsoOptions { RockRidge = rockRidge, Joliet = joliet, IsoLevel = level });

        var expected = IsoinfoPaths(iso, rockRidge ? "-R" : extension);
        var actual = IndexPaths(iso, joliet, udf: false);

        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase), actual.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("mixed case/dir with spaces/file.txt", actual, StringComparer.OrdinalIgnoreCase);
    }

    [ToolFact("xorriso")]
    public void FileTree_OfAPlainLevel1Image_HasNormalisedNames()
    {
        var iso = IsoBuilder.Build(_dir, "level1", new Dictionary<string, byte[]> { ["boot/grub.cfg"] = Bytes("x"), ["README"] = Bytes("r") },
            new IsoOptions { RockRidge = false, Joliet = false, IsoLevel = 1 });

        var paths = IndexPaths(iso, joliet: false, udf: false);

        Assert.Contains("BOOT/GRUB.CFG", paths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("README", paths, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(paths, path => path.Contains(';') || path.EndsWith('.'));
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public async Task Inspect_ReportsVolumeCatalogAndEfiImageContents()
    {
        var efi = IsoBuilder.FatImage(_dir, "efi.img", 2048, new Dictionary<string, string>
        {
            ["EFI/BOOT/BOOTX64.EFI"] = "x64",
            ["EFI/BOOT/BOOTIA32.EFI"] = "ia32",
            ["EFI/BOOT/grub.cfg"] = "cfg",
        });
        var tree = new Dictionary<string, byte[]>
        {
            ["boot/efi.img"] = efi,
            ["isolinux/isolinux.bin"] = IsoBuilder.FakeBootLoader(),
            ["big.bin"] = new byte[40_000],
            ["small.txt"] = Bytes("s"),
        };
        var iso = IsoBuilder.Build(_dir, "details", tree, new IsoOptions
        {
            Label = "DETAILS",
            Hybrid = true,
            BootImages = [new BootImageSpec("isolinux/isolinux.bin"), new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)],
        });

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal("DETAILS", result.Profile.VolumeLabel);
        Assert.Equal("DETAILS", result.Volume!.VolumeId);
        Assert.Equal(new FileInfo(iso).Length, result.Volume.VolumeBytes);
        Assert.Equal(2, result.BootCatalog!.Entries.Count);
        Assert.Equal(["EFI/BOOT/BOOTIA32.EFI", "EFI/BOOT/BOOTX64.EFI", "EFI/BOOT/grub.cfg"], result.BootImageFiles.Order(StringComparer.Ordinal));
        Assert.Equal([WindowsArch.X86, WindowsArch.X64], result.EfiArchitectures);
        Assert.Equal(["boot/efi.img", "big.bin"], result.LargestFiles.Take(2).Select(file => file.Path));
        Assert.Equal(40_000, result.LargestFiles[1].Length);
        // The two boot images, the two data files and the boot catalog xorriso adds.
        Assert.Equal(5, result.FileCount);
        Assert.Contains(result.RootEntries, entry => entry is { Path: "boot", IsDirectory: true });
        Assert.Contains(result.RootEntries, entry => entry is { Path: "small.txt", IsDirectory: false });
        Assert.True(result.Profile.HasEfiBootFiles);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public async Task Inspect_EfiOnlyInsideTheElToritoImage_CountsAsEfiBootFiles()
    {
        var efi = IsoBuilder.FatImage(_dir, "efi.img", 2048, new Dictionary<string, string> { ["EFI/BOOT/BOOTAA64.EFI"] = "arm" });
        var iso = IsoBuilder.Build(_dir, "efi-inner", new Dictionary<string, byte[]> { ["boot/efi.img"] = efi, ["x.txt"] = Bytes("x") },
            new IsoOptions { BootImages = [new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)] });

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.True(result.Profile.HasEfiBootFiles);
        Assert.Equal([WindowsArch.Arm64], result.EfiArchitectures);
        Assert.True(result.Profile.HasElToritoEfi);
        Assert.False(result.Profile.HasElToritoBios);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public async Task Inspect_FloppyEmulationWithDosSystemFiles_IsADosImage()
    {
        var floppy = IsoBuilder.FatImage(_dir, "floppy.img", 1440, new Dictionary<string, string>
        {
            ["IO.SYS"] = "io",
            ["MSDOS.SYS"] = "msdos",
            ["COMMAND.COM"] = "command",
            ["FLASH.EXE"] = "bios update",
        });
        var iso = IsoBuilder.Build(_dir, "bios-update", new Dictionary<string, byte[]> { ["boot.img"] = floppy }, new IsoOptions
        {
            BootImages = [new BootImageSpec("boot.img", Emulation: BootEmulation.Floppy)],
        });

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal(ImageKind.Dos, result.Profile.Kind);
        Assert.Equal("ms-dos", result.Profile.Family);
        Assert.True(result.Profile.HasEmulatedBootImage);
        Assert.True(result.Profile.HasElToritoBios);
        Assert.Contains("FLASH.EXE", result.BootImageFiles);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public async Task Inspect_Hybrid_ReportsMbrAndGptPartitions()
    {
        var efi = IsoBuilder.FatImage(_dir, "efi.img", 2048, new Dictionary<string, string> { ["EFI/BOOT/BOOTX64.EFI"] = "x" });
        var iso = IsoBuilder.Build(_dir, "hybrid", new Dictionary<string, byte[]>
        {
            ["boot/efi.img"] = efi,
            ["isolinux/isolinux.bin"] = IsoBuilder.FakeBootLoader(),
        },
        new IsoOptions
        {
            Hybrid = true,
            Gpt = true,
            BootImages = [new BootImageSpec("isolinux/isolinux.bin"), new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)],
        });

        var result = await new ImageInspector().InspectAsync(iso);
        var layout = result.Layout!;
        var reference = ReferenceTool.Run("sfdisk", "-d", iso).StandardOutput;

        Assert.True(result.Profile.IsHybrid);
        Assert.True(layout.HasMbrSignature);
        Assert.True(layout.HasBootCode);
        Assert.True(layout.HasGpt);
        Assert.True(layout.GptHeaderValid);
        Assert.True(layout.HasEfiSystemPartition);
        Assert.Contains("type=ef", reference, StringComparison.Ordinal);
        Assert.Equal(layout.MbrPartitions.Count, reference.Split('\n').Count(line => line.Contains("start=", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.Warnings, warning => warning.Severity == WarningSeverity.Error);
    }

    [ToolFact("xorriso")]
    public async Task Inspect_IsoWithMbrSignatureButNoPartitionEntries_IsNotHybrid()
    {
        var iso = IsoBuilder.Build(_dir, "mbr-only", new Dictionary<string, byte[]> { ["a.txt"] = Bytes("a") });
        using (var file = new FileStream(iso, FileMode.Open, FileAccess.Write))
        {
            file.Position = 510;
            file.Write([0x55, 0xAA]);
        }

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.False(result.Profile.IsHybrid);
        Assert.True(result.Layout!.HasMbrSignature);
    }

    [ToolFact("genisoimage", "isoinfo", "wimcapture", "wiminfo", "mkfs.vfat", "mcopy")]
    public async Task Inspect_WindowsUdfBridgeIso_ReadsTheWimInside()
    {
        var iso = BuildWindowsIso("win", withEfiLoader: true, includeInstall: true, edition: "Windows 11 Pro");

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal(ImageContainer.IsoUdfBridge, result.Container);
        Assert.Equal(ImageKind.WindowsSetup, result.Profile.Kind);
        Assert.Equal("windows", result.Profile.Family);
        Assert.Equal(WindowsArch.X64, result.Profile.Arch);
        Assert.Equal(26100, result.Profile.WindowsBuild);
        Assert.False(result.Profile.IsHybrid);
        Assert.True(result.Profile.HasBiosBootFiles);
        Assert.True(result.Profile.HasEfiBootFiles);
        Assert.True(result.Profile.HasElToritoBios);
        Assert.True(result.Profile.HasElToritoEfi);
        var windows = result.Windows!;
        Assert.True(windows.HasInstallImage);
        Assert.True(windows.HasBootImage);
        Assert.Equal("Windows 11 Pro", windows.Editions.Single().Name);
        Assert.Equal("Professional", windows.Editions.Single().EditionId);
        Assert.Equal(["en-US"], windows.InstallImages.Single().Metadata.Languages);
        Assert.DoesNotContain(result.Warnings, warning => warning.Severity != WarningSeverity.Info);
    }

    [ToolFact("xorriso", "wimcapture", "wiminfo", "mkfs.vfat", "mcopy")]
    public async Task Inspect_WindowsIsoWithoutUdf_UsesJolietNamesAndStillReadsTheWim()
    {
        var iso = BuildWindowsIso("win-joliet", withEfiLoader: true, includeInstall: true, edition: "Windows 10 Pro", useXorriso: true, arch: 0, build: 19045);

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal(ImageContainer.Iso9660, result.Container);
        Assert.Equal(WindowsArch.X86, result.Profile.Arch);
        Assert.Equal(19045, result.Profile.WindowsBuild);
        Assert.Equal("windows", result.Profile.Family);
    }

    [ToolFact("xorriso", "wimcapture", "wiminfo", "mkfs.vfat", "mcopy")]
    public async Task Inspect_PeMediumWithBootWimOnly_IsWinPe()
    {
        var iso = BuildWindowsIso("pe", withEfiLoader: true, includeInstall: false, edition: "Microsoft Windows PE (x64)", useXorriso: true);

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal(ImageKind.WindowsPe, result.Profile.Kind);
        Assert.Equal("winpe", result.Profile.Family);
        Assert.False(result.Windows!.HasInstallImage);
        Assert.True(result.Windows.HasBootImage);
        Assert.Equal(WindowsArch.X64, result.Profile.Arch);
    }

    [ToolFact("xorriso", "wimcapture", "wiminfo", "mkfs.vfat", "mcopy")]
    public async Task Inspect_Windows7StyleIsoWithoutEfiBootLoader_AsksForTheLoaderToBeExtracted()
    {
        var iso = BuildWindowsIso("win7", withEfiLoader: false, includeInstall: true, edition: "Windows 7 Ultimate", useXorriso: true, build: 7601);

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal(7601, result.Profile.WindowsBuild);
        Assert.True(result.Windows!.NeedsEfiLoaderExtraction);
        Assert.False(result.Profile.HasEfiBootFiles);
        Assert.Contains(result.Warnings, warning => warning.Key == ImageWarningKeys.Win7EfiLoaderMissing);
    }

    [ToolFact("xorriso", "wimcapture", "wiminfo", "mkfs.vfat", "mcopy")]
    public async Task Inspect_WindowsIsoWithCorruptWim_WarnsAndKeepsTheClassification()
    {
        var iso = BuildWindowsIso("win-bad", withEfiLoader: true, includeInstall: true, edition: "Windows 11 Pro", useXorriso: true, corruptInstall: true);

        var result = await new ImageInspector().InspectAsync(iso);

        Assert.Equal(ImageKind.WindowsSetup, result.Profile.Kind);
        Assert.Contains(result.Warnings, warning => warning.Key == ImageWarningKeys.WimUnreadable);
        Assert.Equal(WindowsArch.X64, result.Profile.Arch);
    }

    /// <summary>A Windows setup medium with a real install.wim (and boot.wim) written by wimlib.</summary>
    private string BuildWindowsIso(string name, bool withEfiLoader, bool includeInstall, string edition, bool useXorriso = false, int arch = 9, int build = 26100, bool corruptInstall = false)
    {
        string[] properties =
        [
            $"WINDOWS/ARCH={arch}", "WINDOWS/EDITIONID=" + (includeInstall ? "Professional" : "WindowsPE"),
            $"WINDOWS/VERSION/BUILD={build}", "WINDOWS/LANGUAGES/LANGUAGE=en-US", "WINDOWS/LANGUAGES/DEFAULT=en-US", "WINDOWS/INSTALLATIONTYPE=" + (includeInstall ? "Client" : "WindowsPE"),
        ];

        var files = new Dictionary<string, byte[]>
        {
            ["bootmgr"] = Bytes("bootmgr"),
            ["boot/bcd"] = Bytes("bcd"),
            ["boot/etfsboot.com"] = IsoBuilder.FakeBootLoader(),
            ["setup.exe"] = Bytes("setup"),
        };

        var wim = MakeWim(includeInstall ? "install" : "boot", edition, properties);
        files[includeInstall ? "sources/install.wim" : "sources/boot.wim"] = corruptInstall ? Corrupt(wim) : wim;
        if (includeInstall)
        {
            files["sources/boot.wim"] = MakeWim("boot2", "Microsoft Windows PE (x64)",
                [$"WINDOWS/ARCH={arch}", "WINDOWS/EDITIONID=WindowsPE", $"WINDOWS/VERSION/BUILD={build}", "WINDOWS/INSTALLATIONTYPE=WindowsPE"]);
        }

        var bootImages = new List<BootImageSpec> { new("boot/etfsboot.com", LoadSize: 8) };
        if (withEfiLoader)
        {
            files["efi/microsoft/boot/efisys.bin"] = IsoBuilder.FatImage(_dir, "efisys.img", 2048, new Dictionary<string, string> { ["EFI/BOOT/BOOTX64.EFI"] = "efi" });
            files["efi/boot/bootx64.efi"] = Bytes("efi");
            bootImages.Add(new BootImageSpec("efi/microsoft/boot/efisys.bin", Efi: true, LoadSize: 4096));
        }
        else
        {
            // Windows 7 and Vista SP1 carry the boot manager for UEFI but no loader below efi\boot.
            files["bootmgr.efi"] = Bytes("bootmgr.efi");
        }

        if (useXorriso)
        {
            return IsoBuilder.Build(_dir, name, files, new IsoOptions
            {
                Label = "CCCOMA_X64FRE_EN-US_DV9",
                RockRidge = false,
                Joliet = true,
                BootImages = bootImages,
            });
        }

        var tree = Path.Combine(_dir.Path, "tree-" + name);
        foreach (var (path, content) in files)
        {
            var target = Path.Combine(tree, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, content);
        }

        var iso = _dir.File(name + ".iso");
        ReferenceTool.Run(
            "genisoimage",
            ["-quiet", "-udf", "-J", "-iso-level", "3", "-V", "CCCOMA_X64FRE_EN-US_DV9", "-o", iso,
             "-b", "boot/etfsboot.com", "-no-emul-boot", "-boot-load-size", "8",
             "-eltorito-alt-boot", "-e", "efi/microsoft/boot/efisys.bin", "-no-emul-boot", tree],
            null, null, null);
        return iso;
    }

    private byte[] MakeWim(string name, string imageName, string[] properties)
    {
        var tree = Path.Combine(_dir.Path, "wim-" + name);
        Directory.CreateDirectory(Path.Combine(tree, "Windows"));
        File.WriteAllText(Path.Combine(tree, "Windows", "marker.txt"), name);
        var wim = _dir.File(name + ".wim");
        ReferenceTool.Run("wimcapture", [tree, wim, imageName, imageName, "--compress=LZX"], null, null, null);
        var arguments = new List<string> { wim, "1" };
        foreach (var property in properties)
        {
            arguments.Add("--image-property");
            arguments.Add(property);
        }

        ReferenceTool.Run("wiminfo", arguments, null, null, null);
        return File.ReadAllBytes(wim);
    }

    /// <summary>Destroys the WIM signature so that the file is still present but cannot be parsed.</summary>
    private static byte[] Corrupt(byte[] wim)
    {
        var copy = (byte[])wim.Clone();
        copy[0] = 0;
        return copy;
    }
}
