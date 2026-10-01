// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.FileSystems.Fat;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Core.Tests.Writing.Windows.BootCode;

/// <summary>
/// Builds complete setup media in image files with the writer's own pieces (planner, partition table, FAT formatter,
/// copier, UEFI:NTFS image) and lets real firmware start the loader on them. The loader is a tiny test application.
/// </summary>
public sealed class UefiMediumBootTests : IDisposable
{
    private const string Marker = "BOOTRIX-EFI-TEST-STARTED";

    private readonly TestDirectory _dir = new("bootrix-uefi");

    public void Dispose() => _dir.Dispose();

    private byte[] AssembleTestEfi()
    {
        var output = _dir.File("test.efi");
        ExternalTools.Run("nasm", "-f", "bin", "-o", output, Path.Combine(AppContext.BaseDirectory, "Writing", "Windows", "BootCode", "TestEfi.asm"));
        return File.ReadAllBytes(output);
    }

    /// <summary>The files of a setup medium; the loader that UEFI looks for is the test application.</summary>
    private string WriteSourceTree()
    {
        var files = SetupMediaFixture.BootFiles();
        files["efi/boot/bootx64.efi"] = AssembleTestEfi();
        foreach (var (path, content) in files)
        {
            _dir.Write("source/" + path, content);
        }

        return _dir.File("source");
    }

    private static async Task Copy(string sourceRoot, string destinationRoot, string scratch)
    {
        using var source = DirectoryMediaSource.Scan(sourceRoot);
        var plan = WindowsCopyPlan.Create(source, new WindowsCopyOptions());
        await new WindowsMediaCopier(source, scratch).CopyAsync(plan, destinationRoot, computeHashes: false);
    }

    private static void WriteAt(TempImage image, long offset, byte[] content)
    {
        using var stream = image.Open();
        stream.Position = offset;
        stream.Write(content);
    }

    [RequiresQemuUefiFact("nasm", "mcopy", "fsck.vfat")]
    public async Task Fat32Medium_StartsTheEfiLoaderThatTheCopierWrote()
    {
        var plan = TestMedium.Plan(new TargetOptions { Firmware = TargetFirmware.Uefi }, deviceBytes: 256 * TestMedium.Mib);
        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        using var image = TestMedium.Realize(plan);
        var main = plan.Partitions.Single();

        var staged = _dir.File("staged");
        await Copy(WriteSourceTree(), staged, _dir.File("scratch"));
        var entries = Directory.GetFileSystemEntries(staged);
        ExternalTools.Run("mcopy", ["-s", "-i", $"{image.Path}@@{main.StartBytes}", .. entries, "::"]);

        // The volume as the firmware sees it must be a clean FAT32 file system.
        var volume = _dir.File("volume.img");
        using (var disk = image.Open())
        using (var output = File.Create(volume))
        {
            disk.Position = main.StartBytes;
            var buffer = new byte[main.LengthBytes];
            disk.ReadExactly(buffer);
            output.Write(buffer);
        }

        FatVerifier.Fsck(volume);

        var serial = QemuUefi.Boot($"-drive file={image.Path},format=raw,if=virtio", Marker, TimeSpan.FromSeconds(120));

        Assert.Contains(Marker, serial, StringComparison.Ordinal);
    }

    [RequiresQemuUefiFact("nasm", "mkntfs", "ntfs-3g")]
    public async Task NtfsMedium_StartsThroughTheUefiNtfsPartitionAtTheEnd()
    {
        var plan = TestMedium.Plan(
            new TargetOptions { Firmware = TargetFirmware.Uefi, FileSystem = FileSystemKind.Ntfs },
            deviceBytes: 300 * TestMedium.Mib);
        Assert.True(plan.UsesUefiNtfs);
        var main = plan.Partitions.Single(p => p.Role == PartitionRole.Main);
        var helper = plan.Partitions.Single(p => p.Role == PartitionRole.UefiNtfs);
        using var image = TestMedium.Realize(plan);

        // The NTFS volume is made in a file of its own and mounted through FUSE so the copier can write to it like to any drive.
        var volume = _dir.File("ntfs.img");
        using (var file = File.Create(volume))
        {
            file.SetLength(main.LengthBytes);
        }

        ExternalTools.Run("mkntfs", "-F", "-Q", "-L", "WINTEST", volume);
        var mountPoint = _dir.File("mnt");
        Directory.CreateDirectory(mountPoint);
        var mounted = ExternalTools.Run("ntfs-3g", "-o", "rw", volume, mountPoint);
        if (mounted.ExitCode != 0)
        {
            // No FUSE here (an unprivileged container, a runner without /dev/fuse): nothing to test, not a failure.
            return;
        }

        try
        {
            await Copy(WriteSourceTree(), mountPoint, _dir.File("scratch"));
        }
        finally
        {
            Unmount(mountPoint);
        }

        WriteAt(image, main.StartBytes, File.ReadAllBytes(volume));
        WriteAt(image, helper.StartBytes, UefiNtfsImage.ForSectorSize(512));

        var serial = QemuUefi.Boot($"-drive file={image.Path},format=raw,if=virtio", Marker, TimeSpan.FromSeconds(150));

        Assert.Contains("UEFI:NTFS", serial, StringComparison.Ordinal);
        Assert.Contains(Marker, serial, StringComparison.Ordinal);
    }

    [RequiresQemuUefiFact]
    public void ATestMediumWithoutTheLoader_DoesNotPrintTheMarker()
    {
        // Guards the two tests above against a marker that appears for another reason, such as a leftover in the firmware's output.
        var plan = TestMedium.Plan(new TargetOptions { Firmware = TargetFirmware.Uefi }, deviceBytes: 256 * TestMedium.Mib);
        using var image = TestMedium.Realize(plan);

        var serial = QemuUefi.Boot($"-drive file={image.Path},format=raw,if=virtio", Marker, TimeSpan.FromSeconds(25));

        Assert.DoesNotContain(Marker, serial, StringComparison.Ordinal);
    }

    private static void Unmount(string mountPoint)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (ExternalTools.Run("umount", mountPoint).ExitCode == 0)
            {
                return;
            }

            Thread.Sleep(500);
        }
    }
}
