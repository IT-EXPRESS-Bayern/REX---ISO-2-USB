// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text.Json;
using Bootrix.Core.Images;
using Bootrix.Core.IO;
using Bootrix.Core.Tests.FileSystems.Fat;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Windows;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Writing.Windows.BootCode;

public sealed class ReferenceDiskTests : IDisposable
{
    private readonly TestDirectory _dir = new("bootrix-refdisk");

    public void Dispose() => _dir.Dispose();

    private string Create()
    {
        var path = _dir.File("reference.vhd");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        ReferenceDisk.Write(stream);
        return path;
    }

    [Fact]
    public void TheFile_IsTheDiskFollowedByAFooterTheSnifferRecognises()
    {
        var path = Create();

        Assert.Equal(ReferenceDisk.DiskBytes + 512, new FileInfo(path).Length);
        var footer = File.ReadAllBytes(path)[^512..];
        Assert.True(ImageContainerSniffer.IsFixedVhd(footer));
    }

    [Fact]
    public void TheFooterChecksum_FollowsTheSpecification()
    {
        var footer = ReferenceDisk.Footer(ReferenceDisk.DiskBytes, Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), TimeProvider.System);
        uint sum = 0;
        for (var i = 0; i < footer.Length; i++)
        {
            if (i is < 64 or >= 68)
            {
                sum += footer[i];
            }
        }

        Assert.Equal(~sum, BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(64)));
        Assert.Equal("conectix", System.Text.Encoding.ASCII.GetString(footer, 0, 8));
        Assert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16)));
        Assert.Equal((ulong)ReferenceDisk.DiskBytes, BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(40)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60)));
        Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, footer[68..84]);
    }

    [Fact]
    public void TheTimeStamp_CountsSecondsFromTheYear2000()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 1, 40, TimeSpan.Zero));

        var footer = ReferenceDisk.Footer(ReferenceDisk.DiskBytes, Guid.NewGuid(), time);

        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(24)));
    }

    [Theory]
    [InlineData(147_456L, 963L, 9, 17)]
    [InlineData(2_097_152L, 2080L, 16, 63)]
    [InlineData(2_000_000_000L, 65535L, 16, 255)]
    [InlineData(8_000_000_000L, 65535L, 16, 255)]
    public void TheGeometry_IsTheOneTheSpecificationComputes(long sectors, long cylinders, int heads, int sectorsPerTrack)
    {
        Assert.Equal((cylinders, heads, sectorsPerTrack), ReferenceDisk.Geometry(sectors));
    }

    [RequiresToolFact("qemu-img")]
    public void QemuImg_ReadsItAsAFixedVhdOfTheRightSize()
    {
        var path = Create();

        var result = ExternalTools.Run("qemu-img", "info", "--output=json", "-f", "vpc", path);

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);

        // qemu works out the size of images from unknown creators from the CHS geometry, which rounds down; Windows reads the size field.
        Assert.InRange(document.RootElement.GetProperty("virtual-size").GetInt64(), ReferenceDisk.DiskBytes - 64 * 1024, ReferenceDisk.DiskBytes);
        Assert.Equal("vpc", document.RootElement.GetProperty("format").GetString());
    }

    [RequiresToolFact("qemu-img", "sfdisk", "fsck.vfat", "minfo")]
    public void ThePartitionInside_IsAFat32VolumeFsckAndSfdiskAccept()
    {
        var path = Create();
        var raw = _dir.File("reference.raw");
        ExternalTools.Run("qemu-img", "convert", "-f", "vpc", "-O", "raw", path, raw);

        var dump = ExternalTools.Run("sfdisk", "--dump", raw).Output;
        Assert.Matches(@"start=\s*2048, size=\s*\d+, type=c", dump);
        Assert.InRange(new FileInfo(raw).Length, ReferenceDisk.PartitionOffset + ReferenceDisk.PartitionBytes, ReferenceDisk.DiskBytes);

        var volume = _dir.File("volume.img");
        using (var disk = File.OpenRead(raw))
        using (var output = File.Create(volume))
        {
            new StreamSlice(disk, ReferenceDisk.PartitionOffset, ReferenceDisk.PartitionBytes).CopyTo(output);
        }

        FatVerifier.Fsck(volume);
        var fields = FatVerifier.Minfo(volume);
        Assert.Equal("FAT32", fields["disk type"]);
        Assert.Equal("2048", fields["hidden sectors"]);
    }

    [Fact]
    public async Task TheInspector_SeesAVhd()
    {
        var path = Create();

        var inspection = await new ImageInspector().InspectAsync(path);

        Assert.Equal(ImageContainer.Vhd, inspection.Container);
    }
}
