// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using Bootrix.Core.Boot.Grub;
using Bootrix.Core.Errors;
using Bootrix.Core.Model;

namespace Bootrix.Core.Tests.Writing.Linux;

public class GrubBiosInstallerTests
{
    private static MemoryStream DiskWithTable(int sectors = 8192)
    {
        var disk = new byte[sectors * 512];
        disk[440] = 0x42;
        disk[441] = 0x44;
        disk[446] = 0x80;
        disk[446 + 4] = 0x0C;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 12), 4096);
        disk[510] = 0x55;
        disk[511] = 0xAA;
        return new MemoryStream(disk);
    }

    [Theory]
    [InlineData("boot.img", "7720690bdaf25fd7e3ade7e01b8d0de91e0991653e2a6900011ab23daf6e6032")]
    [InlineData("core-msdos.img", "8e79bae9f727fcfdd2abafe5958345b3f99eab00711793a08377e088ab651842")]
    [InlineData("core-gpt.img", "90648c3d1461ed53e926edc1aa26ecd3b501d1e7470debb7b33675236ca12fcd")]
    public void EmbeddedImages_MatchThePinnedHashes(string name, string sha256)
    {
        var bundle = GrubBundle.Default;
        var bytes = name switch
        {
            "boot.img" => bundle.BootImage.ToArray(),
            "core-msdos.img" => bundle.CoreMbr.ToArray(),
            _ => bundle.CoreGpt.ToArray(),
        };

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void Install_InTheGapOfAnMbrDisk_LinksBootSectorAndCoreImage()
    {
        using var disk = DiskWithTable();

        var result = GrubBiosInstaller.Install(disk, GrubBundle.Default, new GrubInstallRequest(PartitionScheme.Mbr, 1, 2047));

        var bytes = disk.ToArray();
        Assert.Equal(1, result.CoreSector);
        Assert.Equal((GrubBundle.Default.CoreMbr.Length + 511) / 512, result.CoreSectors);

        // boot.img: the sector where the core image starts, and the two NOPs that replace the drive check.
        Assert.Equal(1UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x5C)));
        Assert.Equal(0x90, bytes[0x66]);
        Assert.Equal(0x90, bytes[0x67]);
        Assert.Equal(GrubBundle.Default.BootImage.ToArray()[..0x5C], bytes[..0x5C]);

        // Disk signature, table and boot signature are those of the disk.
        Assert.Equal(DiskWithTable().ToArray()[440..512], bytes[440..512]);

        // First sector of the core image: the block list names the remaining sectors.
        var core = bytes.AsSpan(512, 512);
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(core[500..]));
        Assert.Equal(result.CoreSectors - 1, BinaryPrimitives.ReadUInt16LittleEndian(core[508..]));
        Assert.Equal(0x820, BinaryPrimitives.ReadUInt16LittleEndian(core[510..]));
        Assert.Equal(GrubBundle.Default.CoreMbr.ToArray()[..500], core[..500].ToArray());

        // Everything behind the core image, including the sector in front of the first partition, stays empty.
        Assert.All(bytes.AsSpan(((1 + result.CoreSectors) * 512), 512 * 100).ToArray(), value => Assert.Equal(0, value));
    }

    [Fact]
    public void Install_InTheBiosBootPartitionOfAGptDisk_UsesTheGptCoreImageAndItsStartSector()
    {
        using var disk = DiskWithTable(16384);

        GrubBiosInstaller.Install(disk, GrubBundle.Default, new GrubInstallRequest(PartitionScheme.Gpt, 2048, 2048));

        var bytes = disk.ToArray();
        Assert.Equal(2048UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x5C)));
        var core = bytes.AsSpan(2048 * 512, 512);
        Assert.Equal(2049UL, BinaryPrimitives.ReadUInt64LittleEndian(core[500..]));
        Assert.Equal(GrubBundle.Default.CoreGpt.ToArray()[..500], core[..500].ToArray());
        Assert.Null(GrubBiosInstaller.Verify(disk, GrubBundle.Default, new GrubInstallRequest(PartitionScheme.Gpt, 2048, 2048)));
    }

    [Fact]
    public void Install_TheCoreImageIsPaddedToWholeSectors_AndTheTailIsWritten()
    {
        using var disk = DiskWithTable();

        GrubBiosInstaller.Install(disk, GrubBundle.Default, new GrubInstallRequest(PartitionScheme.Mbr, 1, 2047));

        var core = GrubBundle.Default.CoreMbr.ToArray();
        var onDisk = disk.ToArray().AsSpan(512, core.Length).ToArray();
        Assert.Equal(core[512..], onDisk[512..]);
    }

    [Fact]
    public void Install_WhenTheCoreImageDoesNotFit_FailsBeforeWritingAnything()
    {
        using var disk = DiskWithTable();
        var before = disk.ToArray();

        var error = Assert.Throws<BootrixException>(() => GrubBiosInstaller.Install(disk, GrubBundle.Default, new GrubInstallRequest(PartitionScheme.Mbr, 1, 100)));

        Assert.Equal(ErrorCode.BootloaderInstallFailed, error.Code);
        Assert.Equal(before, disk.ToArray());
    }

    [Fact]
    public void Install_InSectorZero_Fails()
    {
        using var disk = DiskWithTable();

        Assert.Throws<BootrixException>(() => GrubBiosInstaller.Install(disk, GrubBundle.Default, new GrubInstallRequest(PartitionScheme.Mbr, 0, 2048)));
    }

    [Fact]
    public void Verify_DetectsADamagedCoreImageAndBootCode()
    {
        using var disk = DiskWithTable();
        var request = new GrubInstallRequest(PartitionScheme.Mbr, 1, 2047);
        GrubBiosInstaller.Install(disk, GrubBundle.Default, request);
        Assert.Null(GrubBiosInstaller.Verify(disk, GrubBundle.Default, request));

        disk.Position = 512 * 100;
        disk.WriteByte(0xFF);
        Assert.Contains("core image", GrubBiosInstaller.Verify(disk, GrubBundle.Default, request), StringComparison.Ordinal);

        GrubBiosInstaller.Install(disk, GrubBundle.Default, request);
        disk.Position = 0;
        disk.WriteByte(0);
        Assert.Contains("boot code", GrubBiosInstaller.Verify(disk, GrubBundle.Default, request), StringComparison.Ordinal);
    }

    [Fact]
    public void CoreFor_PicksTheImageBuiltForThePartitionTable()
    {
        Assert.True(GrubBundle.Default.CoreFor(PartitionScheme.Mbr).Span.SequenceEqual(GrubBundle.Default.CoreMbr.Span));
        Assert.True(GrubBundle.Default.CoreFor(PartitionScheme.Gpt).Span.SequenceEqual(GrubBundle.Default.CoreGpt.Span));
        Assert.False(GrubBundle.Default.CoreMbr.Span.SequenceEqual(GrubBundle.Default.CoreGpt.Span));
    }

}
