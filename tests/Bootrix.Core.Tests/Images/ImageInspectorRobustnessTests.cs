// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images;

/// <summary>Damaged and hostile images must be reported, never crash the inspector or make it run away.</summary>
public sealed class ImageInspectorRobustnessTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string HybridIso()
    {
        var efi = IsoBuilder.FatImage(_dir, "efi.img", 2048, new Dictionary<string, string> { ["EFI/BOOT/BOOTX64.EFI"] = "x" });
        return IsoBuilder.Build(_dir, "base", new Dictionary<string, byte[]>
        {
            [".disk/info"] = "Ubuntu 24.04"u8.ToArray(),
            ["casper/vmlinuz"] = new byte[100_000],
            ["boot/efi.img"] = efi,
            ["isolinux/isolinux.bin"] = IsoBuilder.FakeBootLoader(),
            ["a/b/c/d.txt"] = "deep"u8.ToArray(),
        },
        new IsoOptions
        {
            Hybrid = true,
            Gpt = true,
            BootImages = [new BootImageSpec("isolinux/isolinux.bin"), new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)],
        });
    }

    private static async Task InspectWithoutCrashing(byte[] data, string name)
    {
        using var stream = new MemoryStream(data);
        var result = await new ImageInspector().InspectAsync(stream, name).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(result.Profile);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public async Task CorruptedIso_NeverEscapesAsAnUnexpectedException()
    {
        var original = File.ReadAllBytes(HybridIso());
        var random = new Random(1234);

        // Volume descriptors, directory records, boot catalog and the El Torito and partition structures all sit in the first part.
        for (var round = 0; round < 80; round++)
        {
            var data = (byte[])original.Clone();
            var flips = random.Next(1, 40);
            for (var i = 0; i < flips; i++)
            {
                var region = random.Next(3) switch { 0 => 0x8000 + random.Next(0x1000), 1 => random.Next(0x20000), _ => random.Next(data.Length) };
                data[Math.Min(region, data.Length - 1)] = (byte)random.Next(256);
            }

            await InspectWithoutCrashing(data, "fuzz.iso");
        }
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public async Task IsoCutAtEveryKindOfBoundary_IsInspectable()
    {
        var original = File.ReadAllBytes(HybridIso());

        foreach (var length in new[] { 0, 1, 511, 512, 513, 0x8000, 0x8000 + 2047, 0x8800, 0x10000, 100_000, original.Length / 2, original.Length - 1 })
        {
            await InspectWithoutCrashing(original.AsSpan(0, length).ToArray(), "cut.iso");
        }
    }

    [Fact]
    public async Task RandomData_WithPlantedSignatures_IsInspectable()
    {
        var random = new Random(77);
        var signatures = new[]
        {
            (Offset: 0x8001, Bytes: "CD001"u8.ToArray()),
            (Offset: 0, Bytes: "MSWIM\0\0\0"u8.ToArray()),
            (Offset: 512, Bytes: "EFI PART"u8.ToArray()),
            (Offset: 510, Bytes: new byte[] { 0x55, 0xAA }),
            (Offset: 0, Bytes: new byte[] { 0x1F, 0x8B, 8 }),
            (Offset: 0, Bytes: "BZh9\x31\x41\x59\x26\x53\x59"u8.ToArray()),
            (Offset: 0, Bytes: new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0 }),
            (Offset: 0, Bytes: new byte[] { 0x28, 0xB5, 0x2F, 0xFD }),
            (Offset: 0, Bytes: "PK\x03\x04"u8.ToArray()),
        };

        for (var round = 0; round < 60; round++)
        {
            var data = new byte[random.Next(1, 200_000)];
            random.NextBytes(data);
            foreach (var signature in signatures.Where(_ => random.Next(2) == 0))
            {
                if (signature.Offset + signature.Bytes.Length <= data.Length)
                {
                    signature.Bytes.CopyTo(data, signature.Offset);
                }
            }

            await InspectWithoutCrashing(data, random.Next(2) == 0 ? "random.iso" : "random.img.xz");
        }
    }

    [ToolFact("sgdisk")]
    public async Task GptWithAbsurdEntryCounts_IsInspectable()
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "gpt.img", 8 * 1024 * 1024, new GptPartitionSpec(2048, 2048, "ef00", "x"));
        var data = File.ReadAllBytes(path);
        BitConverter.TryWriteBytes(data.AsSpan(512 + 80), uint.MaxValue);
        BitConverter.TryWriteBytes(data.AsSpan(512 + 84), 0u);
        BitConverter.TryWriteBytes(data.AsSpan(512 + 72), long.MaxValue);

        await InspectWithoutCrashing(data, "evil.img");
    }

    [Fact]
    public async Task FatBootSectorWithNonsenseGeometry_IsInspectable()
    {
        var data = new byte[2 * 1024 * 1024];
        new Random(5).NextBytes(data);
        data[0] = 0xEB;
        data[2] = 0x90;
        data[11] = 0;
        data[12] = 2;
        data[13] = 4;
        data[14] = 1;
        data[15] = 0;
        data[16] = 2;
        data[21] = 0xF8;
        data[510] = 0x55;
        data[511] = 0xAA;

        await InspectWithoutCrashing(data, "evil.img");
    }

    [Fact]
    public void ParserFailureFilter_TreatsReaderBugsAsDamagedImagesButNotCancellation()
    {
        Assert.True(ImageFileSystem.IsParserFailure(new InvalidCastException()));
        Assert.True(ImageFileSystem.IsParserFailure(new NotImplementedException()));
        Assert.True(ImageFileSystem.IsParserFailure(new InvalidDataException()));
        Assert.False(ImageFileSystem.IsParserFailure(new OperationCanceledException()));
        Assert.False(ImageFileSystem.IsParserFailure(new TaskCanceledException()));
        Assert.False(ImageFileSystem.IsParserFailure(new BootrixException(ErrorCode.ImageUnreadable)));
    }

    [ToolFact("mkfs.vfat", "mcopy", "mmd")]
    public async Task FatDirectoryThatContainsItself_EndsTheWalk()
    {
        var data = IsoBuilder.FatImage(_dir, "loop.img", 1024, new Dictionary<string, string> { ["SUB/X.TXT"] = "x" });

        // 512-byte sectors, FAT12: reserved sectors, FAT copies and root directory come first, clusters follow.
        var sectorsPerCluster = data[13];
        var reserved = BitConverter.ToUInt16(data, 14);
        var fatCopies = data[16];
        var rootEntries = BitConverter.ToUInt16(data, 17);
        var sectorsPerFat = BitConverter.ToUInt16(data, 22);
        var rootStart = (reserved + (fatCopies * sectorsPerFat)) * 512;
        var dataStart = rootStart + (rootEntries * 32);

        var subEntry = Enumerable.Range(0, rootEntries).Select(i => rootStart + (i * 32)).First(at => data.AsSpan(at, 11).SequenceEqual("SUB        "u8));
        var subCluster = BitConverter.ToUInt16(data, subEntry + 26);
        var subData = dataStart + ((subCluster - 2) * sectorsPerCluster * 512);

        // Third slot (after "." and ".."): a directory that points back to the cluster it lives in.
        "LOOP       "u8.CopyTo(data.AsSpan(subData + 64));
        data[subData + 64 + 11] = 0x10;
        BitConverter.TryWriteBytes(data.AsSpan(subData + 64 + 26), subCluster);

        using var stream = new MemoryStream(data);
        var result = await new ImageInspector().InspectAsync(stream, "loop.img").WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(result.Profile);
    }

    [ToolFact("wimcapture", "wiminfo")]
    public async Task WimWithOversizedXmlResource_IsRejectedGracefully()
    {
        var tree = Path.Combine(_dir.Path, "wt");
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(tree, "a"), "a");
        var wim = _dir.File("a.wim");
        ReferenceTool.Run("wimcapture", [tree, wim, "x", "x", "--compress=none"], null, null, null);
        var data = File.ReadAllBytes(wim);
        // Stored size of the XML resource: 7 bytes at offset 0x48; claim 40 MB.
        data[0x48 + 3] = 0x02;

        await InspectWithoutCrashing(data, "big-xml.wim");
    }
}
