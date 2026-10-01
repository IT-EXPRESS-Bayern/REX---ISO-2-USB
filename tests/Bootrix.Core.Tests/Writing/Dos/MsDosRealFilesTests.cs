// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Model;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Dos;

namespace Bootrix.Core.Tests.Writing.Dos;

/// <summary>Skips unless the environment variable holds the path of the real diskcopy.dll and QEMU is installed.</summary>
public sealed class RequiresDiskcopyFactAttribute : FactAttribute
{
    public const string Variable = "BOOTRIX_DISKCOPY_DLL";

    public RequiresDiskcopyFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"{Variable} does not point at diskcopy.dll";
        }
        else if (!ExternalTools.IsAvailable(QemuScreen.Tool))
        {
            Skip = "qemu-system-x86_64 is not installed";
        }
    }
}

/// <summary>
/// The only tests that touch Microsoft's files: whoever has downloaded diskcopy.dll (the file Bootrix fetches at run time)
/// can point <c>BOOTRIX_DISKCOPY_DLL</c> at it and see MS-DOS 8.0 start from images built by the same code the writer uses.
/// Nothing from the file is stored by the tests.
/// </summary>
public class MsDosRealFilesTests
{
    private const long Mib = DosImageBuilder.Mib;

    private static DosSystem Load()
    {
        var dll = File.ReadAllBytes(Environment.GetEnvironmentVariable(RequiresDiskcopyFactAttribute.Variable)!);
        Assert.Equal(MsDosSource.Microsoft.Sha256, Convert.ToHexStringLower(SHA256.HashData(dll)));
        return MsDosFloppy.ToSystem(DiskcopyDll.FindFloppyImage(dll))
            .WithFile(DosFile.Text("\\AUTOEXEC.BAT", "@ECHO OFF\nVER\nECHO BOOTRIX-OK\n"));
    }

    [RequiresDiskcopyFact]
    public void Stick_StartsMsDos_ThroughTheTransplantedBootSector()
    {
        var plan = DosImageBuilder.PlanStick(64 * Mib, FileSystemKind.Fat16);
        using var image = DosImageBuilder.BuildStick(plan, Load());

        var screen = QemuScreen.Boot(
            $"-drive file={image.Path},format=raw,if=none,id=stick -device qemu-xhci -device usb-storage,drive=stick,bootindex=0",
            "BOOTRIX-OK",
            TimeSpan.FromSeconds(90));

        Assert.True(screen.Contains("Windows Millennium", StringComparison.Ordinal) && screen.Contains("BOOTRIX-OK", StringComparison.Ordinal), screen);
    }

    [RequiresDiskcopyFact]
    public void Diskette_StartsMsDos()
    {
        var plan = DosImageBuilder.PlanFloppy();
        using var image = new TempImage(plan.DeviceBytes);
        using (var device = new FileBlockDevice(image.Path, plan.DeviceBytes, create: false))
        {
            SuperfloppyWriter.Write(device, plan, Load());
        }

        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=floppy -boot a", "BOOTRIX-OK", TimeSpan.FromSeconds(90));

        Assert.True(screen.Contains("Windows Millennium", StringComparison.Ordinal) && screen.Contains("BOOTRIX-OK", StringComparison.Ordinal), screen);
    }
}
