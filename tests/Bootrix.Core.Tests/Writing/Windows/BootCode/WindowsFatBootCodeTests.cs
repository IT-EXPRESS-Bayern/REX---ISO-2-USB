// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.IO;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Core.Tests.Writing.Windows.BootCode;

public sealed class WindowsFatBootCodeTests : IDisposable
{
    private readonly TestDirectory _dir = new("bootrix-vbr");

    public void Dispose() => _dir.Dispose();

    /// <summary>A FAT32 volume whose reserved area looks like the one Windows formats: test boot code in sectors 0 to 12.</summary>
    private byte[] ReferenceArea(byte[]? bootCode = null, long bytes = 70 * TestMedium.Mib, int reservedSectors = 32)
    {
        var area = new byte[reservedSectors * 512];
        using var stream = new MemoryStream(new byte[bytes]);
        FatFormatter.Format(stream, new FatFormatOptions
        {
            TotalBytes = bytes,
            Type = FatType.Fat32,
            ReservedSectors = reservedSectors,
            HiddenSectors = 2048,
            BootCode = bootCode,
            AssumeZeroed = true,
        });
        stream.Position = 0;
        stream.ReadExactly(area);
        return area;
    }

    [RequiresToolFact("nasm")]
    public void TheBootCodeOfAReferenceVolume_IsTakenUpToTheLastSectorThatHoldsCode()
    {
        var vbr = TestMedium.AssembleTestVbr(_dir);

        var code = WindowsFatBootCode.FromReservedArea(ReferenceArea(vbr));

        Assert.Equal(13, code.SectorCount);
        Assert.Equal(vbr[0x5A..510], code.Sectors[0x5A..510]);
        Assert.Equal(vbr[(12 * 512)..(13 * 512)], code.Sectors[(12 * 512)..(13 * 512)]);
        Assert.All(code.Sectors[512..1024], value => Assert.Equal(0, value));
    }

    [RequiresToolFact("nasm")]
    public void TheBackupOfTheFirstSectors_IsNotMistakenForCode()
    {
        var vbr = TestMedium.AssembleTestVbr(_dir);
        var area = ReferenceArea(vbr);
        Assert.Equal(area[..512], area[(6 * 512)..(7 * 512)]);

        var code = WindowsFatBootCode.FromReservedArea(area);

        // Sectors 6 to 8 are the backup copy; code ends at sector 12 and nothing after it is claimed.
        Assert.Equal(13, code.SectorCount);
    }

    [RequiresToolFact("nasm")]
    public void ACopyOfSectorTwelveFurtherOn_IsKeptAsPartOfTheCode()
    {
        var vbr = TestMedium.AssembleTestVbr(_dir);
        var area = ReferenceArea(vbr);
        area.AsSpan(12 * 512, 512).CopyTo(area.AsSpan(18 * 512));

        var code = WindowsFatBootCode.FromReservedArea(area);

        Assert.Equal(19, code.SectorCount);
    }

    [RequiresToolFact("nasm")]
    public void OnlyTheAreaThatWasReadIsLookedAt()
    {
        var vbr = TestMedium.AssembleTestVbr(_dir);
        var area = ReferenceArea(vbr)[..(14 * 512)];

        Assert.Equal(13, WindowsFatBootCode.FromReservedArea(area).SectorCount);
    }

    [Fact]
    public void BootCodeWithoutTheLoaderName_IsRefused()
    {
        // The code Bootrix writes on its own (it prints a message and waits for a key) is not NT 6 boot code.
        var ex = Assert.Throws<BootrixException>(() => WindowsFatBootCode.FromReservedArea(ReferenceArea()));

        Assert.Equal(ErrorCode.BootCodeUnavailable, ex.Code);
        Assert.Contains("BOOTMGR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANtfsOrFat16BootSector_IsRefused()
    {
        var fat16 = new byte[16 * 512];
        using (var stream = new MemoryStream(new byte[32 * TestMedium.Mib]))
        {
            FatFormatter.Format(stream, new FatFormatOptions { TotalBytes = 32 * TestMedium.Mib, Type = FatType.Fat16, AssumeZeroed = true });
            stream.Position = 0;
            stream.ReadExactly(fat16);
        }

        var ex = Assert.Throws<BootrixException>(() => WindowsFatBootCode.FromReservedArea(fat16));

        Assert.Equal(ErrorCode.BootCodeUnavailable, ex.Code);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1024)]
    [InlineData(1500)]
    public void ABufferThatIsNotAReservedArea_IsRefused(int length)
    {
        Assert.Throws<BootrixException>(() => WindowsFatBootCode.FromReservedArea(new byte[length]));
    }

    [Fact]
    public void ASectorWithoutTheBootSignature_IsRefused()
    {
        var area = ReferenceArea();
        area[510] = 0;

        Assert.Throws<BootrixException>(() => WindowsFatBootCode.FromReservedArea(area));
    }

    [Fact]
    public void ABootSectorWithoutAnyCode_IsRefused()
    {
        var area = ReferenceArea();
        area.AsSpan(0x5A, 510 - 0x5A).Clear();

        var ex = Assert.Throws<BootrixException>(() => WindowsFatBootCode.FromReservedArea(area));

        Assert.Contains("no code", ex.Message, StringComparison.Ordinal);
    }

    [RequiresToolFact("nasm")]
    public void Matches_ComparesTheCodeButNotTheBpb()
    {
        var code = WindowsFatBootCode.FromReservedArea(ReferenceArea(TestMedium.AssembleTestVbr(_dir)));

        // A volume of another size and position carries the same code in a different BPB.
        using var stream = new MemoryStream(new byte[300 * TestMedium.Mib]);
        FatFormatter.Format(stream, WindowsFatBootCode.Apply(new FatFormatOptions { TotalBytes = 300 * TestMedium.Mib, Type = FatType.Fat32, HiddenSectors = 8192, Label = "OTHER" }, code) with { AssumeZeroed = true });
        var area = stream.ToArray().AsSpan(0, 32 * 512).ToArray();

        Assert.True(WindowsFatBootCode.Matches(area, code));
    }

    [RequiresToolFact("nasm")]
    public void Matches_NoticesAChangedByteInAnyCodeSector()
    {
        var code = WindowsFatBootCode.FromReservedArea(ReferenceArea(TestMedium.AssembleTestVbr(_dir)));
        using var stream = new MemoryStream(new byte[300 * TestMedium.Mib]);
        FatFormatter.Format(stream, WindowsFatBootCode.Apply(new FatFormatOptions { TotalBytes = 300 * TestMedium.Mib, Type = FatType.Fat32 }, code) with { AssumeZeroed = true });
        var good = stream.ToArray().AsSpan(0, 32 * 512).ToArray();

        foreach (var offset in new[] { 0x5A, 0x100, 2 * 512 + 3, 12 * 512 + 10, (12 * 512) + 511 })
        {
            var bad = (byte[])good.Clone();
            bad[offset] ^= 0x40;
            Assert.False(WindowsFatBootCode.Matches(bad, code), $"offset {offset}");
        }

        Assert.False(WindowsFatBootCode.Matches(good.AsSpan(0, 5 * 512), code));
    }

    [RequiresToolFact("nasm")]
    public void Apply_ChangesOnlyTheBootCodeOfTheOptions()
    {
        var code = WindowsFatBootCode.FromReservedArea(ReferenceArea(TestMedium.AssembleTestVbr(_dir)));
        var options = new FatFormatOptions { TotalBytes = 100 * TestMedium.Mib, Label = "X", HiddenSectors = 4096 };

        var applied = WindowsFatBootCode.Apply(options, code);

        Assert.Equal(code.Sectors, applied.BootCode);
        Assert.Equal(options with { BootCode = code.Sectors }, applied);
    }

    // --- the transplant, end to end ---------------------------------------------------------------------

    [RequiresToolFact(QemuScreen.Tool, "nasm", "fsck.vfat")]
    public void BootCodeTakenFromASmallVolume_BootsALargeOneWithAnotherBpb()
    {
        var reference = WindowsFatBootCode.FromReservedArea(ReferenceArea(TestMedium.AssembleTestVbr(_dir)));
        var plan = TestMedium.Plan(new TargetOptions { Firmware = TargetFirmware.Bios }, deviceBytes: 1024 * TestMedium.Mib);
        using var image = TestMedium.Realize(plan, (_, options) => WindowsFatBootCode.Apply(options, reference));
        var main = plan.Partitions.Single();
        using (var stream = image.Open())
        {
            var sector = new byte[512];
            stream.ReadExactly(sector);
            WindowsMbr.Apply(sector, plan);
            stream.Position = 0;
            stream.Write(sector);
        }

        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=ide", "VBR SECTOR 12 REACHED", TimeSpan.FromSeconds(30));

        Assert.Contains("VBR SECTOR 12 REACHED", screen, StringComparison.Ordinal);
        var volume = _dir.File("volume.img");
        using (var disk = image.Open())
        using (var output = File.Create(volume))
        {
            using var slice = new StreamSlice(disk, main.StartBytes, main.LengthBytes);
            slice.CopyTo(output);
        }

        Tests.FileSystems.Fat.FatVerifier.Fsck(volume);
    }

    // --- caching ------------------------------------------------------------------------------------------

    private sealed class CountingSource(Func<int, FatBootSectors> result) : IVbrCodeSource
    {
        private int _calls;

        public int Calls => _calls;

        public async Task<FatBootSectors> ReadFat32Async(string scratchDirectory, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            await Task.Delay(20, cancellationToken);
            return result(call);
        }
    }

    [Fact]
    public async Task TheCachingSource_AsksTheInnerSourceOnceForConcurrentCallers()
    {
        var inner = new CountingSource(_ => new FatBootSectors(new byte[1536]));
        var source = new CachingVbrCodeSource(inner);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => source.ReadFat32Async("scratch", CancellationToken.None)));

        Assert.Equal(1, inner.Calls);
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Same(results[0], await source.ReadFat32Async("scratch", CancellationToken.None));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task TheCachingSource_DoesNotRememberAFailure()
    {
        var inner = new CountingSource(call => call == 1 ? throw new InvalidOperationException("first attempt fails") : new FatBootSectors(new byte[1536]));
        var source = new CachingVbrCodeSource(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadFat32Async("scratch", CancellationToken.None));
        var second = await source.ReadFat32Async("scratch", CancellationToken.None);

        Assert.Equal(3, second.SectorCount);
        Assert.Equal(2, inner.Calls);
    }
}
