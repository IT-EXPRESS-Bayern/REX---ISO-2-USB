// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Model;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Writing.Dos;

/// <summary>
/// Starts the finished images in QEMU with SeaBIOS and reads the VGA text screen. FreeDOS only reaches AUTOEXEC.BAT
/// when the MBR code found the active partition, the boot sector found KERNEL.SYS through the BPB, the kernel
/// read the FAT and COMMAND.COM could be loaded: a boot sector or BPB that is wrong anywhere stops it before.
/// </summary>
public class FreeDosBootTests
{
    private const string Marker = "BOOTRIX-OK";
    private static readonly TimeSpan BootTimeout = TimeSpan.FromSeconds(60);

    private static DosSystem MarkerSystem(DosKernel kernel = DosKernel.Auto, bool forceLba = true) =>
        FreeDosSystem.Create(kernel, floppy: false, forceLba).WithFile(DosFile.Text("\\AUTOEXEC.BAT", $"@ECHO OFF\nECHO {Marker}\n"));

    private static string Ide(string path) => $"-drive file={path},format=raw,if=ide";

    private static string Usb(string path) =>
        $"-drive file={path},format=raw,if=none,id=stick -device qemu-xhci -device usb-storage,drive=stick,bootindex=0";

    [RequiresToolTheory(QemuScreen.Tool)]
    [InlineData(8, FileSystemKind.Fat12, false)]
    [InlineData(64, FileSystemKind.Fat16, false)]
    [InlineData(64, FileSystemKind.Fat16, true)]
    [InlineData(200, FileSystemKind.Fat32, false)]
    [InlineData(200, FileSystemKind.Fat32, true)]
    public void StickBoots_ToTheShell_ForEveryFatType(int megabytes, FileSystemKind fileSystem, bool legacy)
    {
        var plan = DosImageBuilder.PlanStick(megabytes * DosImageBuilder.Mib, fileSystem, legacy);
        using var image = DosImageBuilder.BuildStick(plan, MarkerSystem(forceLba: !legacy), forceBootDrive: legacy);

        var screen = QemuScreen.Boot(Ide(image.Path), Marker, BootTimeout);

        Assert.Contains(Marker, screen, StringComparison.Ordinal);
    }

    [RequiresToolFact(QemuScreen.Tool)]
    public void StickBoots_AlsoWhenTheBiosPresentsItAsUsbStorage()
    {
        var plan = DosImageBuilder.PlanStick(64 * DosImageBuilder.Mib, FileSystemKind.Fat16);
        using var image = DosImageBuilder.BuildStick(plan, MarkerSystem());

        var screen = QemuScreen.Boot(Usb(image.Path), Marker, BootTimeout);

        Assert.Contains(Marker, screen, StringComparison.Ordinal);
    }

    [RequiresToolFact(QemuScreen.Tool)]
    public void StickWithTheDefaultFiles_ShowsTheKernelBanner()
    {
        var plan = DosImageBuilder.PlanStick(64 * DosImageBuilder.Mib, FileSystemKind.Fat16);
        using var image = DosImageBuilder.BuildStick(plan, FreeDosSystem.Create());

        var screen = QemuScreen.Boot(Ide(image.Path), "FreeCOM", BootTimeout);

        Assert.Contains("FreeDOS", screen, StringComparison.Ordinal);
    }
}
