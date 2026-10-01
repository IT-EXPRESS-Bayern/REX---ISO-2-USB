// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;
using FatFileSystem = DiscUtils.Fat.FatFileSystem;
using FatType = Bootrix.Core.FileSystems.Fat.FatType;

namespace Bootrix.Core.Tests.Writing.Linux;

public class SyslinuxInstallerTests
{
    private const long Mib = 1024 * 1024;

    /// <summary>A formatted volume in memory with ldlinux.sys as the first file, the way the writer leaves it before the installer runs.</summary>
    private static SparseMemoryStream Volume(FatType type, long bytes, SyslinuxBundle bundle, int bytesPerSector = 512, int? sectorsPerCluster = null)
    {
        var volume = new SparseMemoryStream(bytes);
        FatFormatter.Format(volume, new FatFormatOptions
        {
            TotalBytes = bytes,
            Type = type,
            BytesPerSector = bytesPerSector,
            SectorsPerCluster = sectorsPerCluster,
            HiddenSectors = 2048,
            AssumeZeroed = true,
        });

        WriteLdlinux(volume, bundle);
        return volume;
    }

    private static void WriteLdlinux(Stream volume, SyslinuxBundle bundle)
    {
        using var fs = new FatFileSystem(volume, DiscUtils.Streams.Ownership.None);
        using var file = fs.OpenFile("ldlinux.sys", FileMode.Create);
        file.Write(SyslinuxInstaller.CreateLdlinuxFile(bundle));
    }

    public static TheoryData<FatType, long, string> Volumes
    {
        get
        {
            var data = new TheoryData<FatType, long, string>();
            foreach (var release in new[] { "6.03", "6.04-pre1" })
            {
                data.Add(FatType.Fat12, 8 * Mib, release);
                data.Add(FatType.Fat16, 64 * Mib, release);
                data.Add(FatType.Fat32, 256 * Mib, release);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Volumes))]
    public void Install_OnAFreshVolume_PassesItsOwnVerification(FatType type, long bytes, string release)
    {
        var bundle = SyslinuxBundle.Shipped.Single(b => b.Id == release);
        using var volume = Volume(type, bytes, bundle);

        var result = SyslinuxInstaller.Install(volume, bundle);

        Assert.Null(SyslinuxInstaller.Verify(volume));
        Assert.Equal(release, result.Release);
        Assert.Equal(1, result.Extents);
    }

    [Fact]
    public void Install_KeepsTheBiosParameterBlockAndSignatureOfTheVolume()
    {
        var bundle = SyslinuxBundle.Shipped[^1];
        using var volume = Volume(FatType.Fat32, 128 * Mib, bundle);
        var before = Sector(volume, 0);

        SyslinuxInstaller.Install(volume, bundle);

        var after = Sector(volume, 0);
        Assert.Equal(before[11..90], after[11..90]);
        Assert.Equal(0x55, after[510]);
        Assert.Equal(0xAA, after[511]);
        Assert.Equal("SYSLINUX", System.Text.Encoding.ASCII.GetString(after, 3, 8));
        Assert.NotEqual(before[90..510], after[90..510]);
    }

    [Fact]
    public void Install_WritesTheLoaderIntoTheSectorsOfTheFile_AndLeavesTheFileUsable()
    {
        var bundle = SyslinuxBundle.Shipped[0];
        using var volume = Volume(FatType.Fat32, 128 * Mib, bundle);

        SyslinuxInstaller.Install(volume, bundle);

        using var fs = new FatFileSystem(volume, DiscUtils.Streams.Ownership.None);
        using var file = fs.OpenFile("ldlinux.sys", FileMode.Open, FileAccess.Read);
        var content = new byte[file.Length];
        file.ReadExactly(content);
        Assert.Equal(69632, content.Length);

        // Only the patch area (checksum, sector map, ADV pointers) differs from the released loader.
        var core = bundle.Core.ToArray();
        var differing = Enumerable.Range(0, core.Length).Count(i => core[i] != content[i]);
        Assert.InRange(differing, 1, 1500);
    }

    [Fact]
    public void Install_TwiceOnTheSameVolume_StillVerifies()
    {
        var bundle = SyslinuxBundle.Shipped[1];
        using var volume = Volume(FatType.Fat16, 64 * Mib, bundle);

        SyslinuxInstaller.Install(volume, bundle);
        SyslinuxInstaller.Install(volume, bundle);

        Assert.Null(SyslinuxInstaller.Verify(volume));
    }

    [Fact]
    public void Install_SingleSectorReads_SetsTheMaxTransferInTheLoader()
    {
        var bundle = SyslinuxBundle.Shipped[1];
        using var normal = Volume(FatType.Fat32, 128 * Mib, bundle);
        using var careful = Volume(FatType.Fat32, 128 * Mib, bundle);

        SyslinuxInstaller.Install(normal, bundle);
        SyslinuxInstaller.Install(careful, bundle, new SyslinuxInstallOptions { SingleSectorReads = true });

        var a = Loader(normal);
        var b = Loader(careful);
        var area = IndexOf(a, 0x3eb202fe);
        Assert.NotEqual(1, BitConverter.ToUInt16(a, area + 20));
        Assert.Equal(1, BitConverter.ToUInt16(b, area + 20));
        Assert.Null(SyslinuxInstaller.Verify(careful));
    }

    [Fact]
    public void Install_FileInSeveralPieces_ListsEveryPieceAndVerifies()
    {
        var bundle = SyslinuxBundle.Shipped[1];
        var volume = new SparseMemoryStream(16 * Mib);
        FatFormatter.Format(volume, new FatFormatOptions { TotalBytes = 16 * Mib, Type = FatType.Fat16, SectorsPerCluster = 1, AssumeZeroed = true });
        Fragment(volume, bundle, holes: 12);

        var result = SyslinuxInstaller.Install(volume, bundle);

        Assert.True(result.Extents >= 12, $"{result.Extents} extents");
        Assert.Null(SyslinuxInstaller.Verify(volume));
    }

    [Fact]
    public void Install_FileInAsManyPiecesAsPossible_FitsTheExtentTable()
    {
        // Every second cluster is taken, so no two sectors of the 133 data sectors of the loader are neighbours.
        var bundle = SyslinuxBundle.Shipped[1];
        var volume = new SparseMemoryStream(16 * Mib);
        FatFormatter.Format(volume, new FatFormatOptions { TotalBytes = 16 * Mib, Type = FatType.Fat16, SectorsPerCluster = 1, AssumeZeroed = true });
        Fragment(volume, bundle, holes: 140);

        var result = SyslinuxInstaller.Install(volume, bundle);

        Assert.True(result.Extents > 100, $"{result.Extents} extents");
        Assert.Null(SyslinuxInstaller.Verify(volume));
    }

    [Fact]
    public void Install_WithoutLdlinuxSys_Fails()
    {
        var bundle = SyslinuxBundle.Shipped[0];
        var volume = new SparseMemoryStream(64 * Mib);
        FatFormatter.Format(volume, new FatFormatOptions { TotalBytes = 64 * Mib, Type = FatType.Fat16, AssumeZeroed = true });

        var error = Assert.Throws<BootrixException>(() => SyslinuxInstaller.Install(volume, bundle));

        Assert.Equal(ErrorCode.BootloaderInstallFailed, error.Code);
    }

    [Fact]
    public void Install_FileThatIsTooShort_Fails()
    {
        var bundle = SyslinuxBundle.Shipped[0];
        var volume = new SparseMemoryStream(64 * Mib);
        FatFormatter.Format(volume, new FatFormatOptions { TotalBytes = 64 * Mib, Type = FatType.Fat16, AssumeZeroed = true });
        using (var fs = new FatFileSystem(volume, DiscUtils.Streams.Ownership.None))
        using (var file = fs.OpenFile("ldlinux.sys", FileMode.Create))
        {
            file.Write(new byte[10_000]);
        }

        Assert.Throws<BootrixException>(() => SyslinuxInstaller.Install(volume, bundle));
    }

    [Fact]
    public void Install_On4096ByteSectors_IsRefused()
    {
        var bundle = SyslinuxBundle.Shipped[0];
        using var volume = Volume(FatType.Fat32, 2048 * Mib, bundle, bytesPerSector: 4096);

        var error = Assert.Throws<BootrixException>(() => SyslinuxInstaller.Install(volume, bundle));

        Assert.Contains("4096", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_OnAnythingButFat_IsRefused()
    {
        var bundle = SyslinuxBundle.Shipped[0];
        var volume = new MemoryStream(new byte[1024 * 1024]);

        Assert.Throws<BootrixException>(() => SyslinuxInstaller.Install(volume, bundle));
    }

    [Fact]
    public void Verify_DetectsADamagedLoader()
    {
        var bundle = SyslinuxBundle.Shipped[1];
        using var volume = Volume(FatType.Fat32, 128 * Mib, bundle);
        SyslinuxInstaller.Install(volume, bundle);
        Assert.Null(SyslinuxInstaller.Verify(volume));

        // One flipped bit in the middle of the loader.
        var offset = FirstLdlinuxSector(volume) * 512L + 20_000;
        volume.Position = offset;
        var value = volume.ReadByte();
        volume.Position = offset;
        volume.WriteByte((byte)(value ^ 0x10));

        Assert.Contains("checksum", SyslinuxInstaller.Verify(volume), StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_DetectsAVolumeWithoutSyslinuxBootSector()
    {
        var bundle = SyslinuxBundle.Shipped[1];
        using var volume = Volume(FatType.Fat32, 128 * Mib, bundle);

        Assert.Contains("boot sector", SyslinuxInstaller.Verify(volume), StringComparison.Ordinal);
    }

    [RequiresToolFact("syslinux", "mcopy")]
    public void Verify_AcceptsAnInstallationMadeByTheReferenceInstaller()
    {
        var path = Path.Combine(Path.GetTempPath(), "bootrix-ref-" + Guid.NewGuid().ToString("N")[..8] + ".img");
        try
        {
            using (var image = new FileStream(path, FileMode.CreateNew))
            {
                image.SetLength(64 * Mib);
                FatFormatter.Format(image, new FatFormatOptions { TotalBytes = 64 * Mib, Type = FatType.Fat32, AssumeZeroed = true });
            }

            var run = ExternalTools.Run("syslinux", "--install", path);
            Assert.True(run.ExitCode == 0, run.Combined);

            using var volume = new FileStream(path, FileMode.Open, FileAccess.Read);
            Assert.Null(SyslinuxInstaller.Verify(volume));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Sector(Stream volume, long sector)
    {
        var buffer = new byte[512];
        volume.Position = sector * 512;
        volume.ReadExactly(buffer);
        return buffer;
    }

    private static byte[] Loader(Stream volume)
    {
        using var fs = new FatFileSystem(volume, DiscUtils.Streams.Ownership.None);
        using var file = fs.OpenFile("ldlinux.sys", FileMode.Open, FileAccess.Read);
        var content = new byte[file.Length];
        file.ReadExactly(content);
        return content;
    }

    private static int IndexOf(byte[] data, uint magic)
    {
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            if (BitConverter.ToUInt32(data, i) == magic)
            {
                return i;
            }
        }

        throw new InvalidOperationException("patch area not found");
    }

    private static long FirstLdlinuxSector(Stream volume)
    {
        var locator = typeof(FatFormatter).Assembly.GetType("Bootrix.Core.FileSystems.Fat.FatFileLocator")!;
        var instance = locator.GetMethod("Open")!.Invoke(null, [volume])!;
        var found = locator.GetMethod("FindInRoot")!.Invoke(instance, ["LDLINUX SYS"])!;
        var cluster = (uint)found.GetType().GetField("Item1")!.GetValue(found)!;
        return ((long[])locator.GetMethod("FileSectors")!.Invoke(instance, [cluster, 1])!)[0];
    }

    /// <summary>Leaves holes of one cluster in front of the free space, so that ldlinux.sys has to be stored in as many pieces.</summary>
    private static void Fragment(Stream volume, SyslinuxBundle bundle, int holes)
    {
        using var fs = new FatFileSystem(volume, DiscUtils.Streams.Ownership.None);
        for (var i = 0; i < holes * 2; i++)
        {
            using var filler = fs.OpenFile($"f{i:D3}.bin", FileMode.Create);
            filler.Write(new byte[512]);
        }

        for (var i = 0; i < holes * 2; i += 2)
        {
            fs.DeleteFile($"f{i:D3}.bin");
        }

        using var file = fs.OpenFile("ldlinux.sys", FileMode.Create);
        file.Write(SyslinuxInstaller.CreateLdlinuxFile(bundle));
    }
}
