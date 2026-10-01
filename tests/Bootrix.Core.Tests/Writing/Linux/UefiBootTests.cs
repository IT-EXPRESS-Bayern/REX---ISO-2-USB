// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Tests.Writing.Linux.Support;
using Bootrix.Core.Writing.Linux;

namespace Bootrix.Core.Tests.Writing.Linux;

/// <summary>Skips a test when one of the tools or files it works with does not exist on this machine.</summary>
public sealed class RequiresToolsAndFilesFactAttribute : FactAttribute
{
    public RequiresToolsAndFilesFactAttribute(string[] tools, string[] files)
    {
        var missing = tools.Where(tool => !ExternalTools.IsAvailable(tool)).Concat(files.Where(file => !File.Exists(file))).ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Required on this machine: {string.Join(", ", missing)}";
        }
    }
}

/// <summary>
/// UEFI start of a stick written in file-copy mode: the firmware (OVMF) finds \EFI\BOOT\BOOTX64.EFI on the FAT partition
/// the files were copied to. The loader on the image is a stand-alone GRUB whose configuration reports what it sees.
/// </summary>
public sealed class UefiBootTests : IDisposable
{
    private const string Ovmf = "/usr/share/ovmf/OVMF.fd";
    private const string SignedGrub = "/usr/lib/grub/x86_64-efi-signed/grubx64.efi.signed";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "bootrix-uefi-" + Guid.NewGuid().ToString("N")[..10]);

    public UefiBootTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Leftover scratch files are not worth a failed run.
        }
    }

    /// <summary>A GRUB for x86_64 EFI that looks for the kernel of the mini image on whatever disk it was started from.</summary>
    private byte[] StandaloneGrub()
    {
        var config = Path.Combine(_work, "embedded.cfg");
        File.WriteAllText(
            config,
            "serial --unit=0 --speed=115200\n" +
            "terminal_input serial\n" +
            "terminal_output serial\n" +
            "echo BOOTRIX-EFI-OK\n" +
            "search --no-floppy --file --set=root /live/vmlinuz\n" +
            "echo BOOTRIX-ROOT=$root\n" +
            "ls ($root)/live/\n" +
            "echo BOOTRIX-LS-DONE\n");
        var output = Path.Combine(_work, "BOOTX64.EFI");
        var result = ExternalTools.Run(
            "grub-mkstandalone",
            "-O", "x86_64-efi",
            "-o", output,
            "--modules=part_gpt part_msdos fat serial terminfo ls echo search search_fs_file normal",
            $"boot/grub/grub.cfg={config}");
        Assert.True(result.ExitCode == 0, result.Combined);
        return File.ReadAllBytes(output);
    }

    private string Iso(byte[] loader, string name) => MiniLinuxIso.Build(
        Path.Combine(_work, name),
        _work,
        new MiniIsoOptions
        {
            EfiLoader = false,
            ExtraFiles = new Dictionary<string, byte[]> { ["EFI/BOOT/BOOTX64.EFI"] = loader },
        });

    [RequiresToolsAndFilesFact(["xorriso", "mcopy", "qemu-system-x86_64", "grub-mkstandalone"], [Ovmf])]
    public void UefiStick_StartsTheLoaderCopiedFromTheImage_AndFindsTheKernelFiles()
    {
        using var stick = TestStick.Create(Iso(StandaloneGrub(), "uefi.iso"), new TargetOptions { Mode = WriteMode.Extract, Firmware = TargetFirmware.Uefi });

        var serial = QemuSerial.Boot(["-bios", Ovmf, .. QemuSerial.HardDisk(stick.ImagePath)], "BOOTRIX-LS-DONE", TimeSpan.FromSeconds(120));

        Assert.Equal(PartitionScheme.Gpt, stick.Plan.Scheme);
        Assert.Equal(BiosLoader.None, stick.Result.Bios.Loader);
        Assert.Contains("BOOTRIX-EFI-OK", serial, StringComparison.Ordinal);
        Assert.Contains("BOOTRIX-ROOT=hd0,gpt", serial, StringComparison.Ordinal);
        Assert.Contains("vmlinuz", serial, StringComparison.Ordinal);
        Assert.Contains("initrd.img", serial, StringComparison.Ordinal);
    }

    [RequiresToolsAndFilesFact(["xorriso", "mcopy"], [SignedGrub])]
    public void SignedLoaderOnTheImage_IsAnalysedWhileCopying()
    {
        using var stick = TestStick.Create(Iso(File.ReadAllBytes(SignedGrub), "signed.iso"), new TargetOptions { Mode = WriteMode.Extract, Firmware = TargetFirmware.Uefi });

        var report = Assert.IsType<EfiAnalysisReport>(stick.Result.Efi);

        var loader = Assert.Single(report.Files, f => f.Path.EndsWith("BOOTX64.EFI", StringComparison.OrdinalIgnoreCase));
        Assert.True(loader.IsEfiImage);
        Assert.True(loader.IsSigned);
        Assert.NotEqual(EfiMediaVerdict.NoEfiFiles, report.Verdict);
        Assert.Contains(stick.Result.Notices, n => report.Summary.Any(m => m.Key == n.Key));
    }
}
