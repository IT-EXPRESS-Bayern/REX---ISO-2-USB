// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Tests.Writing.Linux.Support;
using Bootrix.Core.Writing.Linux;

namespace Bootrix.Core.Tests.Writing.Linux;

/// <summary>
/// Writes a small Linux ISO the way the Linux writer does (everything except the Windows mounting) and boots the result in
/// QEMU. The kernel on the stick is a stub that prints its command line on the serial port, so what the boot loader
/// really loaded and passed on is read from the guest, not assumed.
/// </summary>
public sealed class LinuxStickBootTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bootrix-iso-" + Guid.NewGuid().ToString("N")[..10]);

    public LinuxStickBootTests() => Directory.CreateDirectory(_work);

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

    private string Iso(MiniIsoOptions? options = null) => MiniLinuxIso.Build(Path.Combine(_work, Guid.NewGuid().ToString("N")[..6] + ".iso"), _work, options ?? new MiniIsoOptions());

    private static string Boot(TestStick stick) =>
        QemuSerial.Boot(QemuSerial.HardDisk(stick.ImagePath), "BOOTRIX-INITRD size=", QemuSerial.DefaultTimeout);

    private static TargetOptions Extract(PartitionScheme scheme = PartitionScheme.Auto, int persistenceMegabytes = 0, string? label = null) =>
        new() { Mode = WriteMode.Extract, Scheme = scheme, PersistenceMegabytes = persistenceMegabytes, Label = label };

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Syslinux_OnMbrFat_BootsTheKernelWithTheCommandLineOfTheImage()
    {
        using var stick = TestStick.Create(Iso());

        var serial = Boot(stick);

        Assert.Equal(BiosLoader.Syslinux, stick.Result.Bios.Loader);
        Assert.Contains("BOOTRIX-KERNEL cmdline=BOOT_IMAGE=/live/vmlinuz initrd=/live/initrd.img boot=live components quiet root=live:CDLABEL=MINI_LIVE", serial, StringComparison.Ordinal);
        Assert.Contains("BOOTRIX-INITRD size=00001000", serial, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Syslinux_RedirectConfigurationAndLdlinuxModule_AreInTheRoot()
    {
        using var stick = TestStick.Create(Iso());

        var listing = ExternalTools.Run("mdir", "-i", stick.MainSpec, "-a", "-b", "::").Output;

        Assert.Contains("ldlinux.sys", listing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ldlinux.c32", listing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("syslinux.cfg", listing, StringComparison.OrdinalIgnoreCase);
        var redirect = ExternalTools.Run("mtype", "-i", stick.MainSpec, "::syslinux.cfg").Output;
        Assert.Contains("CONFIG /isolinux/isolinux.cfg", redirect, StringComparison.Ordinal);
        Assert.Contains("APPEND /isolinux/", redirect, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64", "fsck.vfat")]
    public void Syslinux_StickPassesFsckAndKeepsTheTreeOfTheImage()
    {
        using var stick = TestStick.Create(Iso());

        var fsck = stick.FsckMainPartition();

        Assert.True(fsck.ExitCode == 0, fsck.Combined);
        var listing = ExternalTools.Run("mdir", "-i", stick.MainSpec, "-/", "-b", "::").Output;
        Assert.Contains("::/live/vmlinuz", listing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("::/EFI/BOOT/BOOTX64.EFI", listing, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Syslinux_WithDebianPersistence_AddsThePersistenceParameter()
    {
        using var stick = TestStick.Create(Iso(), Extract(persistenceMegabytes: 64));

        var serial = Boot(stick);

        Assert.Contains("boot=live persistence components quiet", serial, StringComparison.Ordinal);
        Assert.Equal(PartitionRole.Persistence, stick.Plan.Partitions[^1].Role);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Syslinux_WithAnotherLabelOnTheMedium_RewritesTheLabelOnTheCommandLine()
    {
        var iso = Iso(new MiniIsoOptions { Label = "Mini Live 1.0" });
        using var stick = TestStick.Create(iso);

        var serial = Boot(stick);

        Assert.Contains("root=live:CDLABEL=MINI\\x20LIVE\\x201", serial, StringComparison.Ordinal);
        Assert.DoesNotContain("1.0", serial.Split("BOOTRIX-KERNEL", 2)[1], StringComparison.Ordinal);
        Assert.Contains(stick.Result.Notices, n => n.Key == LinuxNoticeKeys.LabelRewritten);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Syslinux_WithCustomLabel_UsesItInTheBootConfiguration()
    {
        using var stick = TestStick.Create(Iso(), Extract(label: "BOOTRIXSTK"));

        var serial = Boot(stick);

        Assert.Contains("root=live:CDLABEL=BOOTRIXSTK", serial, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Syslinux_OnTheOlderSixOhThree_UsesThatReleaseAndBoots()
    {
        var iso = Iso(new MiniIsoOptions { IsolinuxBanner = "ISOLINUX 6.03 2014-10-06" });
        using var stick = TestStick.Create(iso);

        var serial = Boot(stick);

        Assert.Equal("6.03", stick.Result.Bios.Syslinux!.Bundle.Id);
        Assert.Contains("BOOTRIX-KERNEL cmdline=BOOT_IMAGE=/live/vmlinuz", serial, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Grub_OnMbrFat_FindsTheMenuAndBootsTheKernel()
    {
        var iso = Iso(new MiniIsoOptions { Flavor = MiniFlavor.Ubuntu, Label = "MINI_UBUNTU" });
        using var stick = TestStick.Create(iso);

        var serial = Boot(stick);

        Assert.Equal(BiosLoader.Grub, stick.Result.Bios.Loader);
        Assert.Contains("BOOTRIX-KERNEL cmdline=BOOT_IMAGE=/casper/vmlinuz boot=casper components quiet root=live:CDLABEL=MINI_UBUNTU", serial, StringComparison.Ordinal);
        Assert.Contains("BOOTRIX-INITRD size=00001000", serial, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64", "sgdisk")]
    public void Grub_OnGpt_UsesTheBiosBootPartitionAndBoots()
    {
        var iso = Iso(new MiniIsoOptions { Flavor = MiniFlavor.Ubuntu, Label = "MINI_UBUNTU" });
        using var stick = TestStick.Create(iso, Extract(PartitionScheme.Gpt));

        var serial = Boot(stick);

        Assert.Equal(PartitionRole.BiosBoot, stick.Plan.Partitions[0].Role);
        Assert.Contains("BOOTRIX-KERNEL cmdline=BOOT_IMAGE=/casper/vmlinuz boot=casper", serial, StringComparison.Ordinal);
        var verify = ExternalTools.Run("sgdisk", "-v", stick.ImagePath);
        Assert.True(verify.ExitCode == 0, verify.Combined);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Grub_WithCasperPersistence_AddsPersistent()
    {
        var iso = Iso(new MiniIsoOptions { Flavor = MiniFlavor.Ubuntu, Label = "MINI_UBUNTU" });
        using var stick = TestStick.Create(iso, Extract(persistenceMegabytes: 64));

        var serial = Boot(stick);

        Assert.Contains("boot=casper persistent components quiet", serial, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Grub_ReplacesSyslinuxWhenTheImageUsesAnOlderGeneration()
    {
        var iso = Iso(new MiniIsoOptions { IsolinuxBanner = "ISOLINUX 4.05 2011-12-09", IncludeLdlinux = false });
        using var stick = TestStick.Create(iso);

        var serial = Boot(stick);

        Assert.Equal(BiosLoader.Grub, stick.Result.Bios.Loader);
        Assert.Contains(stick.Result.Notices, n => n.Key == LinuxNoticeKeys.GrubInsteadOfSyslinuxVersion);
        Assert.Contains("BOOTRIX-KERNEL cmdline=BOOT_IMAGE=/live/vmlinuz", serial, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Syslinux_OnImageOfAnOlderGenerationWithoutGrub_ReplacesTheModulesAndBoots()
    {
        var iso = Iso(new MiniIsoOptions { IsolinuxBanner = "ISOLINUX 4.05 2011-12-09", IncludeLdlinux = false, Grub = false });
        using var stick = TestStick.Create(iso);

        var serial = Boot(stick);

        Assert.Equal(BiosLoader.Syslinux, stick.Result.Bios.Loader);
        Assert.Equal(Bootrix.Core.Boot.Syslinux.SyslinuxMatch.Different, SyslinuxMatchOf(stick));
        Assert.Contains(stick.Result.Notices, n => n.Key == LinuxNoticeKeys.SyslinuxVersionDiffers);
        Assert.Contains("BOOTRIX-KERNEL cmdline=BOOT_IMAGE=/live/vmlinuz", serial, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso", "mcopy", "qemu-system-x86_64")]
    public void Grub_MenuInBootGrub2_IsReachedThroughAStub()
    {
        var iso = Iso(new MiniIsoOptions { Isolinux = false, GrubDirectory = "boot/grub2", GrubBiosModules = true });
        using var stick = TestStick.Create(iso);

        var serial = Boot(stick);

        Assert.Equal(BiosLoader.Grub, stick.Result.Bios.Loader);
        Assert.Contains(stick.Result.Notices, n => n.Key == LinuxNoticeKeys.GrubMenuRelocated);
        Assert.Contains("BOOTRIX-KERNEL cmdline=BOOT_IMAGE=/live/vmlinuz", serial, StringComparison.Ordinal);
    }

    private static Bootrix.Core.Boot.Syslinux.SyslinuxMatch SyslinuxMatchOf(TestStick stick) => stick.Result.Bios.Syslinux!.Match;
}
