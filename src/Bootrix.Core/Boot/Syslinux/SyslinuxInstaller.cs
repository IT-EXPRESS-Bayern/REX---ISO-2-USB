// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;

namespace Bootrix.Core.Boot.Syslinux;

public sealed record SyslinuxInstallOptions
{
    /// <summary>Reads one sector per BIOS call. Slower, but the only thing some old BIOSes survive.</summary>
    public bool SingleSectorReads { get; init; }
}

/// <param name="Release">Id of the shipped release that was installed.</param>
/// <param name="FirstSector">Volume sector of the first sector of ldlinux.sys.</param>
/// <param name="Extents">How many separate pieces ldlinux.sys occupies.</param>
public sealed record SyslinuxInstallResult(string Release, long FirstSector, int Extents);

/// <summary>
/// Installs Syslinux onto a FAT volume the way <c>syslinux --install</c> does: the loader (ldlinux.sys) is a normal
/// file in the root directory, the boot sector is told where its first sector is, and the loader is told where the
/// rest is. Everything is written through the volume stream, so the same code serves a disk image under test and a
/// dismounted partition on a real disk.
/// </summary>
public static class SyslinuxInstaller
{
    public const string LdlinuxFileName = "ldlinux.sys";

    private const string ShortName = "LDLINUX SYS";
    private const string Component = "Syslinux";

    /// <summary>
    /// The content the caller has to store as <c>ldlinux.sys</c> before <see cref="Install"/> runs, with the hidden,
    /// system and read-only attributes. It should be the first file on a fresh volume so that it stays in one piece.
    /// </summary>
    public static byte[] CreateLdlinuxFile(SyslinuxBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return SyslinuxImage.CreateFile(bundle.Core.Span);
    }

    /// <summary>
    /// Writes the sector map into ldlinux.sys and Syslinux's boot code into the first sector of the volume.
    /// The volume must not be mounted by an operating system while this runs.
    /// </summary>
    /// <exception cref="BootrixException">The volume is not a FAT volume with 512-byte sectors, or ldlinux.sys is missing or too fragmented.</exception>
    public static SyslinuxInstallResult Install(Stream volume, SyslinuxBundle bundle, SyslinuxInstallOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(bundle);
        try
        {
            return InstallCore(volume, bundle, options ?? new SyslinuxInstallOptions());
        }
        catch (InvalidDataException ex)
        {
            throw Failed(ex.Message, ex);
        }
    }

    /// <summary>Checks an installation without changing anything; returns null when it is sound, otherwise a description of the first problem.</summary>
    public static string? Verify(Stream volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        try
        {
            var locator = FatFileLocator.Open(volume);
            if (locator.BytesPerSector != SyslinuxImage.SectorSize)
            {
                return "the volume does not use 512-byte sectors";
            }

            var boot = ReadSector(volume, 0);
            if (!boot.AsSpan(3, 8).SequenceEqual("SYSLINUX"u8))
            {
                return "the boot sector is not a Syslinux boot sector";
            }

            if (locator.FindInRoot(ShortName) is not { } file)
            {
                return "ldlinux.sys is missing from the root directory";
            }

            if (file.Size < SyslinuxImage.AdvSectors * SyslinuxImage.SectorSize || (file.Size - (SyslinuxImage.AdvSectors * SyslinuxImage.SectorSize)) % SyslinuxImage.SectorSize != 0)
            {
                return "ldlinux.sys has an unexpected size";
            }

            var imageLength = (int)file.Size - (SyslinuxImage.AdvSectors * SyslinuxImage.SectorSize);
            var sectors = locator.FileSectors(file.FirstCluster, (int)(file.Size / SyslinuxImage.SectorSize));
            var image = new byte[imageLength];
            for (var i = 0; i < imageLength / SyslinuxImage.SectorSize; i++)
            {
                ReadSector(volume, sectors[i]).CopyTo(image, i * SyslinuxImage.SectorSize);
            }

            return SyslinuxImage.Validate(image, sectors, boot);
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
    }

    private static SyslinuxInstallResult InstallCore(Stream volume, SyslinuxBundle bundle, SyslinuxInstallOptions options)
    {
        var locator = FatFileLocator.Open(volume);
        if (locator.BytesPerSector != SyslinuxImage.SectorSize)
        {
            throw Failed($"the volume uses {locator.BytesPerSector}-byte sectors, Syslinux needs 512");
        }

        var existing = ReadSector(volume, 0);
        if (existing[21] != 0xF0 && existing[21] < 0xF8)
        {
            throw Failed("the first sector has no FAT media descriptor");
        }

        var core = bundle.Core.Span;
        var sectorCount = SyslinuxImage.FileSectors(core.Length);
        var file = locator.FindInRoot(ShortName) ?? throw Failed("ldlinux.sys has not been written to the root directory");
        if (file.Size < (uint)(sectorCount * SyslinuxImage.SectorSize))
        {
            throw Failed("ldlinux.sys on the volume is shorter than the loader");
        }

        var sectors = locator.FileSectors(file.FirstCluster, sectorCount);
        var patched = SyslinuxImage.Patch(core, bundle.BootSector.Span, sectors, options.SingleSectorReads, default);

        var imageSectors = patched.Core.Length / SyslinuxImage.SectorSize;
        for (var i = 0; i < imageSectors; i++)
        {
            WriteSector(volume, sectors[i], patched.Core.AsSpan(i * SyslinuxImage.SectorSize, SyslinuxImage.SectorSize));
        }

        var adv = new byte[SyslinuxImage.AdvSectors * SyslinuxImage.SectorSize];
        SyslinuxImage.ResetAdv(adv);
        for (var i = 0; i < SyslinuxImage.AdvSectors; i++)
        {
            WriteSector(volume, sectors[imageSectors + i], adv.AsSpan(i * SyslinuxImage.SectorSize, SyslinuxImage.SectorSize));
        }

        WriteSector(volume, 0, SyslinuxImage.MakeBootSector(patched.BootSectorTemplate, existing));
        volume.Flush();

        if (Verify(volume) is { } problem)
        {
            throw Failed("the installation does not verify: " + problem);
        }

        return new SyslinuxInstallResult(bundle.Id, sectors[0], CountExtents(sectors, imageSectors));
    }

    private static int CountExtents(long[] sectors, int imageSectors)
    {
        var extents = 1;
        for (var i = 2; i < imageSectors; i++)
        {
            if (sectors[i] != sectors[i - 1] + 1)
            {
                extents++;
            }
        }

        return extents;
    }

    private static byte[] ReadSector(Stream volume, long sector)
    {
        var buffer = new byte[SyslinuxImage.SectorSize];
        volume.Position = sector * SyslinuxImage.SectorSize;
        volume.ReadExactly(buffer);
        return buffer;
    }

    private static void WriteSector(Stream volume, long sector, ReadOnlySpan<byte> data)
    {
        volume.Position = sector * SyslinuxImage.SectorSize;
        volume.Write(data);
    }

    private static BootrixException Failed(string detail, Exception? inner = null) =>
        new(ErrorCode.BootloaderInstallFailed, detail, inner) { Arguments = [Component, detail] };
}
