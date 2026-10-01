// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.FileSystems.Fat;

/// <summary>Runs the default boot sector code on an emulated PC; no other check proves that hand-assembled machine code works.</summary>
public class FatBootStubTests
{
    [RequiresToolFact(QemuScreen.Tool)]
    public void FloppyWithoutBootCode_ShowsTheMessageAndWaitsForAKey()
    {
        using var image = new TempImage(1_474_560);
        using (var stream = image.Open())
        {
            FatFormatter.Format(stream, FloppyPreset.All.Single(p => p.Name == "1.44M").ToOptions());
        }

        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=floppy -boot a", "not bootable", TimeSpan.FromSeconds(15));

        Assert.Contains("This disk is not bootable. Insert a system disk and press any key", screen, StringComparison.Ordinal);
    }

    [RequiresToolFact(QemuScreen.Tool)]
    public void HardDiskVolumeWithoutBootCode_ShowsTheMessage()
    {
        // A superfloppy hard disk image: the BIOS runs the volume's boot sector straight from LBA 0.
        using var image = new TempImage(64 * 1024 * 1024);
        using (var stream = image.Open())
        {
            FatFormatter.Format(stream, new FatFormatOptions
            {
                TotalBytes = 64 * 1024 * 1024,
                Type = FatType.Fat32,
                DriveNumber = 0x80,
                AssumeZeroed = true,
            });
        }

        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=ide", "not bootable", TimeSpan.FromSeconds(15));

        Assert.Contains("This disk is not bootable", screen, StringComparison.Ordinal);
    }
}
