// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;

namespace Bootrix.Core.Tests.Images.Support;

/// <summary>A synthetic ISO tree that mimics the marker files of one distribution, with the classification it must get.</summary>
public sealed record IsoFamilyCase(
    string Family,
    ImageKind Kind,
    string Label,
    IReadOnlyDictionary<string, string> Files,
    string? Release = null);

/// <summary>
/// Marker files as found on the real media (paths taken from the distributions' ISO layouts); the contents are
/// irrelevant except for the small text files the fingerprints read.
/// </summary>
public static class IsoFamilyCases
{
    private static readonly Dictionary<string, IsoFamilyCase> Cases = new(StringComparer.Ordinal)
    {
        ["ubuntu"] = new("ubuntu", ImageKind.LinuxHybrid, "Ubuntu 24.04 LTS amd64", Files(
            (".disk/info", "Ubuntu 24.04 LTS \"Noble Numbat\" - Release amd64 (20240424)"),
            ("casper/vmlinuz", "k"), ("casper/initrd", "i"), ("casper/filesystem.squashfs", "s"), ("boot/grub/grub.cfg", "g")),
            "Ubuntu 24.04 LTS \"Noble Numbat\" - Release amd64 (20240424)"),
        ["kubuntu"] = new("ubuntu", ImageKind.LinuxHybrid, "Kubuntu 22.04", Files(
            (".disk/info", "Kubuntu 22.04.4 LTS \"Jammy Jellyfish\" - Release amd64"), ("casper/vmlinuz", "k"))),
        ["casper-only"] = new("ubuntu", ImageKind.LinuxHybrid, "CASPER", Files(("casper/vmlinuz", "k"), ("casper/filesystem.squashfs", "s"))),
        ["linuxmint"] = new("linuxmint", ImageKind.LinuxHybrid, "Linux Mint 21.3 Cinnamon 64-bit", Files(
            (".disk/info", "Linux Mint 21.3 \"Virginia\" - Release amd64 20240105"), ("casper/vmlinuz", "k"), ("casper/filesystem.squashfs", "s"))),
        ["zorin"] = new("zorin", ImageKind.LinuxHybrid, "Zorin-OS-17", Files(
            (".disk/info", "Zorin OS 17 Core 64-bit - Release"), ("casper/vmlinuz", "k"))),
        ["popos"] = new("popos", ImageKind.LinuxHybrid, "POP-OS 22.04 intel", Files(
            ("casper_pop-os_22.04_amd64_intel_100/vmlinuz.efi", "k"), ("casper_pop-os_22.04_amd64_intel_100/initrd.gz", "i"))),
        ["debian"] = new("debian", ImageKind.LinuxHybrid, "Debian 12.5.0 amd64 n", Files(
            (".disk/info", "Debian GNU/Linux 12.5.0 \"Bookworm\" - Official amd64 DVD Binary-1 with firmware 20240210-11:49"),
            ("dists/bookworm/Release", "r"), ("install.amd/vmlinuz", "k"), ("pool/main/a/a.deb", "d"))),
        ["debian-live"] = new("debian-live", ImageKind.LinuxHybrid, "d-live 12.5.0 gn amd64", Files(
            (".disk/info", "Debian GNU/Linux 12.5.0 \"Bookworm\" - Official amd64 LIVE/INSTALL Binary 20240210-11:49"),
            ("live/vmlinuz-6.1.0-18-amd64", "k"), ("live/initrd.img-6.1.0-18-amd64", "i"), ("live/filesystem.squashfs", "s"))),
        ["live-boot-generic"] = new("debian-live", ImageKind.LinuxHybrid, "SOMELIVE", Files(
            ("live/vmlinuz", "k"), ("live/filesystem.squashfs", "s"))),
        ["kali"] = new("kali", ImageKind.LinuxHybrid, "Kali Live", Files(
            (".disk/info", "Kali GNU/Linux 2024.1 \"kali-rolling\" - Official amd64 live-build 20240225"),
            ("live/vmlinuz-6.6.9-amd64", "k"), ("live/filesystem.squashfs", "s"))),
        ["parrot"] = new("parrot", ImageKind.LinuxHybrid, "Parrot-security-6.0_amd64", Files(
            ("live/vmlinuz", "k"), ("live/filesystem.squashfs", "s"))),
        ["fedora"] = new("fedora", ImageKind.LinuxHybrid, "Fedora-WS-Live-40-1-14", Files(
            (".treeinfo", "[general]\nfamily = Fedora\nname = Fedora\nversion = 40\narch = x86_64\n"),
            ("LiveOS/squashfs.img", "s"), ("images/pxeboot/vmlinuz", "k")),
            "Fedora 40"),
        ["rocky"] = new("rhel", ImageKind.LinuxHybrid, "Rocky-9-3-x86_64-dvd", Files(
            (".treeinfo", "[general]\nfamily = Rocky Linux\nname = Rocky Linux\nversion = 9.3\n"),
            ("images/pxeboot/vmlinuz", "k"), ("BaseOS/Packages/a.rpm", "r")),
            "Rocky Linux 9.3"),
        ["alma"] = new("rhel", ImageKind.LinuxHybrid, "AlmaLinux-9-3-x86_64-dvd", Files(
            (".treeinfo", "[general]\nfamily = AlmaLinux\nname = AlmaLinux\nversion = 9.3\n"), ("images/pxeboot/vmlinuz", "k"))),
        ["arch"] = new("arch", ImageKind.LinuxHybrid, "ARCH_202401", Files(
            ("arch/boot/x86_64/vmlinuz-linux", "k"), ("arch/x86_64/airootfs.sfs", "s"), ("loader/entries/01-archiso-x86_64.conf", "c"))),
        ["arch-custom-installdir"] = new("arch", ImageKind.LinuxHybrid, "CUSTOM", Files(
            ("mydistro/boot/x86_64/vmlinuz-linux", "k"), ("mydistro/x86_64/airootfs.sfs", "s"))),
        ["manjaro"] = new("manjaro", ImageKind.LinuxHybrid, "MANJARO_KDE_2312", Files(
            (".miso", ""), ("manjaro/x86_64/livefs.sfs", "s"), ("boot/vmlinuz-x86_64", "k"))),
        ["endeavouros"] = new("endeavouros", ImageKind.LinuxHybrid, "EOS_202403", Files(
            ("arch/boot/x86_64/vmlinuz-linux", "k"), ("arch/x86_64/airootfs.sfs", "s"))),
        ["systemrescue"] = new("systemrescue", ImageKind.LinuxHybrid, "RESCUE1102", Files(
            ("sysresccd/boot/x86_64/vmlinuz", "k"), ("sysresccd/x86_64/airootfs.sfs", "s"), ("sysresccd/pkglist.x86_64.txt", "p"))),
        ["opensuse-label"] = new("opensuse", ImageKind.LinuxHybrid, "openSUSE-Tumbleweed-DVD-x86_64", Files(
            ("boot/x86_64/loader/linux", "k"), ("boot/x86_64/loader/initrd", "i"), ("media.1/products", "p"))),
        ["opensuse-content"] = new("opensuse", ImageKind.LinuxHybrid, "INSTALL", Files(
            ("content", "PRODUCT openSUSE-Leap\nVERSION 15.5\n"), ("boot/x86_64/loader/linux", "k")),
            "openSUSE-Leap"),
        ["proxmox"] = new("proxmox", ImageKind.LinuxHybrid, "PVE", Files(
            ("proxmox/packages/pve-manager.deb", "d"), ("boot/linux26", "k"), ("boot/initrd.img", "i"))),
        ["proxmox-marker"] = new("proxmox", ImageKind.LinuxHybrid, "PROXMOX-VE", Files(
            (".pve-cd-id.cfg", "id"), ("boot/linux26", "k"))),
        ["truenas"] = new("truenas", ImageKind.LinuxHybrid, "TRUENAS", Files(
            ("TrueNAS-SCALE.update", "u"), ("boot/vmlinuz", "k"))),
        ["memtest-label"] = new("memtest86plus", ImageKind.LinuxHybrid, "MEMTEST86+", Files(
            ("boot/memtest.bin", "m"), ("boot/grub/grub.cfg", "g"))),
        ["memtest-files"] = new("memtest86plus", ImageKind.LinuxHybrid, "MT", Files(
            ("boot/memtest.bin", "m"), ("boot/grub/grub.cfg", "g"))),
        ["memtest86"] = new("memtest86", ImageKind.LinuxHybrid, "MEMTEST", Files(
            ("EFI/BOOT/BOOTX64.efi", "e"), ("EFI/BOOT/mt86.png", "p"), ("EFI/BOOT/unifont.bin", "u"))),
        ["clonezilla"] = new("clonezilla", ImageKind.LinuxHybrid, "CLONEZILLA", Files(
            ("Clonezilla-Live-Version", "clonezilla-live-3.1.2-9-amd64"), ("live/vmlinuz", "k"), ("live/filesystem.squashfs", "s")),
            "clonezilla-live-3.1.2-9-amd64"),
        ["gparted"] = new("gparted", ImageKind.LinuxHybrid, "GPARTED-LIVE", Files(
            ("GParted-Live-Version", "gparted-live-1.5.0-6-amd64"), ("live/vmlinuz", "k"), ("live/filesystem.squashfs", "s")),
            "gparted-live-1.5.0-6-amd64"),
        ["tails-iso"] = new("tails", ImageKind.LinuxHybrid, "TAILS", Files(
            ("live/Tails.module", "t"), ("live/vmlinuz", "k"))),
        ["esxi"] = new("esxi", ImageKind.OtherOs, "ESXI-8.0U2", Files(
            ("boot.cfg", "bootstate=0"), ("mboot.c32", "m"), ("isolinux.cfg", "c"), ("s.v00", "s"), ("b.b00", "b"))),
        ["reactos"] = new("reactos", ImageKind.OtherOs, "ReactOS", Files(
            ("reactos/system32/ntoskrnl.exe", "n"), ("loader/setupldr.sys", "l"))),
        ["kolibrios"] = new("kolibrios", ImageKind.OtherOs, "KOLIBRIOS", Files(("kolibri.img", "k"), ("README.txt", "r"))),
        ["freebsd"] = new("freebsd", ImageKind.Bsd, "FREEBSD_INSTALL", Files(
            ("boot/kernel/kernel", "k"), ("boot/loader", "l"), ("usr/freebsd-dist/base.txz", "b"))),
        ["openbsd"] = new("openbsd", ImageKind.Bsd, "OpenBSD/amd64 7.5", Files(
            ("7.5/amd64/bsd.rd", "k"), ("7.5/amd64/cdbr", "c"), ("7.5/amd64/cdboot", "b"), ("etc/boot.conf", "b"))),
        ["netbsd"] = new("netbsd", ImageKind.Bsd, "NETBSD_10", Files(
            ("binary/sets/base.tar.xz", "b"), ("installation/miniroot/miniroot.kmod", "m"))),
        ["freedos"] = new("freedos", ImageKind.Dos, "FD14CD", Files(
            ("FDOS/BIN/FORMAT.EXE", "f"), ("KERNEL.SYS", "k"), ("FDCONFIG.SYS", "c"))),
        ["windows-nt5"] = new("windows-nt5", ImageKind.WindowsSetup, "WXPFPP_EN", Files(
            ("I386/TXTSETUP.SIF", "[SetupData]\nSetupSourceDevice = x"), ("I386/NTDETECT.COM", "n"), ("I386/WINNT32.EXE", "w"), ("I386/SETUPLDR.BIN", "l"))),
        ["winpe-xp"] = new("winpe-xp", ImageKind.WindowsPe, "BARTPE", Files(
            ("I386/TXTSETUP.SIF", "[SetupData]\nOsLoadOptions = \"/fastdetect /minint\""), ("I386/NTDETECT.COM", "n"), ("MININT/SYSTEM32/a.dll", "a"))),
        ["winpe-xp-by-txtsetup"] = new("winpe-xp", ImageKind.WindowsPe, "PE", Files(
            ("I386/TXTSETUP.SIF", "[SetupData]\nOsLoadOptions=/minint"), ("I386/NTDETECT.COM", "n"))),
        ["hirens-pe"] = new("hirens-pe", ImageKind.WindowsPe, "HBCD_PE_x64", Files(
            ("sources/boot.wim", "w"), ("bootmgr", "b"), ("boot/bcd", "b"))),
        ["hirens-pe-ini"] = new("hirens-pe", ImageKind.WindowsPe, "HIREN", Files(
            ("HBCD_PE.ini", "i"), ("sources/boot.wim", "w"), ("bootmgr", "b"))),
        ["winpe"] = new("winpe", ImageKind.WindowsPe, "WINPE", Files(
            ("sources/boot.wim", "w"), ("bootmgr", "b"), ("boot/bcd", "b"), ("boot/boot.sdi", "s"))),
    };

    public static IEnumerable<string> Names => Cases.Keys;

    public static IsoFamilyCase Get(string name) => Cases[name];

    private static Dictionary<string, string> Files(params (string Path, string Content)[] files) =>
        files.ToDictionary(file => file.Path, file => file.Content, StringComparer.Ordinal);
}
