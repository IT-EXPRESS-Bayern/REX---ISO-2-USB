// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images;

public sealed class ImageInspectorDiskTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    /// <summary>An MBR disk whose first partition holds a FAT volume with the given files.</summary>
    private string DiskWithFatPartition(string name, IReadOnlyDictionary<string, string> files, string type = "c", params MbrPartitionSpec[] more)
    {
        var fat = IsoBuilder.FatImage(_dir, name + "-fat.img", 8 * 1024, files);
        var partitions = new List<MbrPartitionSpec> { new(2048, fat.Length / 512, type, Bootable: true) };
        partitions.AddRange(more);
        var path = DiskImageBuilder.CreateMbr(_dir, name + ".img", 64 * MiB, [.. partitions]);
        DiskImageBuilder.WriteAt(path, 2048 * 512, fat);
        DiskImageBuilder.WriteAt(path, 0, [0xFA, 0x31, 0xC0]);
        return path;
    }

    private static async Task<ImageInspection> Inspect(string path) => await new ImageInspector().InspectAsync(path);

    [ToolFact("sfdisk", "mkfs.vfat", "mcopy")]
    public async Task Inspect_RaspberryPiStyleImage_IsRecognisedFromItsBootPartition()
    {
        var path = DiskWithFatPartition("pi", new Dictionary<string, string>
        {
            ["cmdline.txt"] = "console=serial0,115200 root=PARTUUID=x",
            ["config.txt"] = "arm_64bit=1",
            ["start4.elf"] = "firmware",
            ["kernel8.img"] = "kernel",
        });

        var result = await Inspect(path);

        Assert.Equal(ImageContainer.RawDisk, result.Container);
        Assert.Equal("raspberrypi", result.Profile.Family);
        Assert.Equal(ImageKind.RawDisk, result.Profile.Kind);
        Assert.True(result.Profile.IsHybrid);
        Assert.Equal(64 * MiB, result.Profile.ImageBytes);
        Assert.Equal(64 * MiB, result.Profile.TotalBytes);
        Assert.False(result.IsTruncated);
    }

    [ToolFact("sfdisk", "mkfs.vfat", "mcopy")]
    public async Task Inspect_FreeDosUsbImage_IsADosImage()
    {
        var path = DiskWithFatPartition("fd", new Dictionary<string, string>
        {
            ["KERNEL.SYS"] = "kernel",
            ["COMMAND.COM"] = "command",
            ["FDCONFIG.SYS"] = "config",
        });

        var result = await Inspect(path);

        Assert.Equal("freedos", result.Profile.Family);
        Assert.Equal(ImageKind.Dos, result.Profile.Kind);
    }

    [ToolFact("sgdisk", "sfdisk")]
    public async Task Inspect_TailsStyleGptImage_IsRecognisedByThePartitionName()
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "tails.img", 64 * MiB,
            new GptPartitionSpec(2048, 8192, "ef00", "EFI System Partition"),
            new GptPartitionSpec(16384, 30000, "0700", "Tails"));

        var result = await Inspect(path);

        Assert.Equal("tails", result.Profile.Family);
        Assert.Equal(ImageKind.RawDisk, result.Profile.Kind);
        Assert.True(result.Profile.HasEspPartition);
        Assert.True(result.Layout!.GptHeaderValid);
    }

    [ToolFact("sgdisk")]
    public async Task Inspect_ChromeOsStyleGptImage_IsRecognisedByPartitionNames()
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "chromeos.bin", 64 * MiB,
            new GptPartitionSpec(2048, 4096, "8300", "STATE"),
            new GptPartitionSpec(8192, 4096, "7f00", "KERN-A"),
            new GptPartitionSpec(16384, 8192, "7f01", "ROOT-A"),
            new GptPartitionSpec(40000, 4096, "ef00", "EFI-SYSTEM"));

        var result = await Inspect(path);

        Assert.Equal("chromeos", result.Profile.Family);
        Assert.Equal(ImageKind.RawDisk, result.Profile.Kind);
    }

    [ToolFact("sgdisk")]
    public async Task Inspect_FreeBsdMemstickLayout_IsBsd()
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "FreeBSD-14.0-RELEASE-amd64-memstick.img", 64 * MiB,
            new GptPartitionSpec(2048, 1024, "ef00", "efiboot0"),
            new GptPartitionSpec(4096, 1024, "a501", "bootfs"),
            new GptPartitionSpec(8192, 30000, "a503", "rootfs"));

        var result = await Inspect(path);

        Assert.Equal("freebsd", result.Profile.Family);
        Assert.Equal(ImageKind.Bsd, result.Profile.Kind);
        Assert.Equal(ImageContainer.RawDisk, result.Container);
    }

    [ToolTheory("sfdisk")]
    [InlineData("a6", "openbsd")]
    [InlineData("a9", "netbsd")]
    [InlineData("a5", "freebsd")]
    public async Task Inspect_MbrWithBsdPartitionType_IsBsd(string type, string family)
    {
        var path = DiskImageBuilder.CreateMbr(_dir, "bsd-" + type + ".img", 32 * MiB, new MbrPartitionSpec(64, 60000, type, Bootable: true));

        var result = await Inspect(path);

        Assert.Equal(family, result.Profile.Family);
        Assert.Equal(ImageKind.Bsd, result.Profile.Kind);
    }

    [ToolFact("sfdisk", "mkfs.vfat", "mcopy", "wimcapture", "wiminfo")]
    public async Task Inspect_WindowsInstallerStickImage_ReadsTheWimFromThePartition()
    {
        var tree = Path.Combine(_dir.Path, "wimtree");
        Directory.CreateDirectory(Path.Combine(tree, "Windows"));
        File.WriteAllText(Path.Combine(tree, "Windows", "a.txt"), "a");
        var wimFile = _dir.File("install.wim");
        ExternalTool.Run("wimcapture", [tree, wimFile, "Windows 11 Home", "Home", "--compress=LZX"], null, null, null);
        ExternalTool.Run("wiminfo", [wimFile, "1", "--image-property", "WINDOWS/ARCH=12", "--image-property", "WINDOWS/VERSION/BUILD=26100"], null, null, null);

        var fat = _dir.File("stick-fat.img");
        File.WriteAllBytes(fat, new byte[16 * MiB]);
        ExternalTool.Run("mkfs.vfat", ["-F", "16", "-n", "WININSTALL", fat], null, null, null);
        ExternalTool.Run("mmd", ["-i", fat, "::sources"], null, null, null);
        ExternalTool.Run("mcopy", ["-i", fat, wimFile, "::sources/install.wim"], null, null, null);
        ExternalTool.Run("mcopy", ["-i", fat, wimFile, "::sources/boot.wim"], null, null, null);
        ExternalTool.Run("mcopy", ["-i", fat, _dir.Write("bootmgr", [1]), "::bootmgr"], null, null, null);
        var disk = DiskImageBuilder.CreateMbr(_dir, "stick.img", 64 * MiB, new MbrPartitionSpec(2048, 16 * MiB / 512, "e", Bootable: true));
        DiskImageBuilder.WriteAt(disk, 2048 * 512, File.ReadAllBytes(fat));

        var result = await Inspect(disk);

        Assert.Equal("windows", result.Profile.Family);
        Assert.Equal(ImageKind.WindowsSetup, result.Profile.Kind);
        Assert.Equal(WindowsArch.Arm64, result.Profile.Arch);
        Assert.Equal(26100, result.Profile.WindowsBuild);
        Assert.Equal(ImageContainer.RawDisk, result.Container);
    }

    [ToolTheory("mkfs.vfat", "mcopy")]
    [InlineData("IO.SYS", "ms-dos")]
    [InlineData("KERNEL.SYS", "freedos")]
    public async Task Inspect_FloppyImage_IsAVolumeNotADisk(string systemFile, string family)
    {
        var floppy = IsoBuilder.FatImage(_dir, "floppy.img", 1440, new Dictionary<string, string> { [systemFile] = "sys", ["COMMAND.COM"] = "cmd" });
        var path = _dir.Write("boot.img", floppy);

        var result = await Inspect(path);

        Assert.Equal(ImageContainer.FatVolume, result.Container);
        Assert.Equal(family, result.Profile.Family);
        Assert.Equal(ImageKind.Dos, result.Profile.Kind);
        Assert.False(result.Profile.IsHybrid);
        Assert.Equal(1440 * 1024, result.Profile.ImageBytes);
    }

    [ToolFact("mkfs.vfat", "mcopy")]
    public async Task Inspect_FatVolumeWithoutSystemFiles_IsData()
    {
        var volume = IsoBuilder.FatImage(_dir, "plain.img", 2048, new Dictionary<string, string> { ["README.TXT"] = "x" });

        var result = await Inspect(_dir.Write("plain.img", volume));

        Assert.Equal(ImageContainer.FatVolume, result.Container);
        Assert.Equal(ImageKind.Data, result.Profile.Kind);
        Assert.Null(result.Profile.Family);
    }

    [ToolFact("sgdisk", "losetup")]
    public async Task Inspect_4KnGptImage_CarriesAHint()
    {
        var path = DiskImageBuilder.Create(_dir, "4kn.img", 32 * MiB);
        var device = ExternalTool.RunUnchecked("losetup", ["--find", "--show", "--sector-size", "4096", path]);
        if (device.ExitCode != 0)
        {
            return;
        }

        var loop = device.StandardOutput.Trim();
        try
        {
            ExternalTool.Run("sgdisk", "-n", "1:256:+1024", "-t", "1:ef00", loop);
        }
        finally
        {
            ExternalTool.RunUnchecked("losetup", ["-d", loop]);
        }

        var result = await Inspect(path);

        Assert.Contains(result.Warnings, warning => warning.Key == ImageWarningKeys.Sector4kImage);
    }

    [ToolFact("qemu-img", "sfdisk")]
    public async Task Inspect_FixedVhd_IsAnalysedLikeARawDisk()
    {
        var raw = DiskImageBuilder.CreateMbr(_dir, "vhd-src.img", 16 * MiB, new MbrPartitionSpec(2048, 8192, "ef", Bootable: true));
        DiskImageBuilder.WriteAt(raw, 0, [0xFA, 0x31, 0xC0]);
        var vhd = _dir.File("fixed.vhd");
        ExternalTool.Run("qemu-img", "convert", "-f", "raw", "-O", "vpc", "-o", "subformat=fixed", raw, vhd);

        var result = await Inspect(vhd);

        Assert.Equal(ImageContainer.Vhd, result.Container);
        Assert.Equal(ImageKind.RawDisk, result.Profile.Kind);
        Assert.True(result.Profile.HasEspPartition);
        Assert.True(result.Profile.IsHybrid);
        Assert.False(result.IsTruncated);
    }

    [ToolTheory("qemu-img")]
    [InlineData("vpc", "subformat=dynamic", ImageContainer.Vhd)]
    [InlineData("vhdx", "subformat=dynamic", ImageContainer.Vhdx)]
    public async Task Inspect_DynamicVirtualDisks_AreReportedByContainer(string format, string option, ImageContainer expected)
    {
        var raw = DiskImageBuilder.Create(_dir, "v-src.img", 8 * MiB);
        var target = _dir.File("disk." + format);
        ExternalTool.Run("qemu-img", "convert", "-f", "raw", "-O", format, "-o", option, raw, target);

        var result = await Inspect(target);

        Assert.Equal(expected, result.Container);
        Assert.Equal(ImageKind.RawDisk, result.Profile.Kind);
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task Inspect_ArbitraryData_IsUnknownContainerAndData()
    {
        var noise = new byte[20_000];
        new Random(5).NextBytes(noise);
        var path = _dir.Write("noise.bin", noise);

        var result = await Inspect(path);

        Assert.Equal(ImageContainer.Unknown, result.Container);
        Assert.Equal(ImageKind.Data, result.Profile.Kind);
        Assert.Null(result.Profile.Family);
    }

    [Fact]
    public async Task Inspect_UdifTrailer_IsAnAppleImage()
    {
        var data = new byte[4096];
        "koly"u8.CopyTo(data.AsSpan(data.Length - 512));

        var result = await Inspect(_dir.Write("mac.dmg", data));

        Assert.Equal(ImageContainer.AppleImage, result.Container);
        Assert.Equal(ImageKind.Apple, result.Profile.Kind);
    }

    [Fact]
    public async Task Inspect_ApmDisk_IsApple()
    {
        var data = new byte[64 * 1024];
        data[0] = (byte)'E';
        data[1] = (byte)'R';
        data[2] = 2;

        var result = await Inspect(_dir.Write("apm.img", data));

        Assert.Equal(ImageContainer.RawDisk, result.Container);
        Assert.Equal(ImageKind.Apple, result.Profile.Kind);
        Assert.True(result.Layout!.HasApm);
    }

    [Fact]
    public async Task Inspect_FfuHeader_IsReportedAsFfu()
    {
        var data = new byte[8192];
        BitConverter.TryWriteBytes(data.AsSpan(0), 32);
        "SignedImage "u8.CopyTo(data.AsSpan(4));

        var result = await Inspect(_dir.Write("flash.ffu", data));

        Assert.Equal(ImageContainer.Ffu, result.Container);
    }

    [Fact]
    public async Task Inspect_Stream_WorksWithoutAFileName()
    {
        using var stream = new MemoryStream(new byte[100_000]);

        var result = await new ImageInspector().InspectAsync(stream);

        Assert.Equal(ImageContainer.Unknown, result.Container);
        Assert.Null(result.FileName);
    }

    [Fact]
    public async Task Inspect_NonSeekableStream_IsRejected()
    {
        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream();

        await Assert.ThrowsAsync<ArgumentException>(() => new ImageInspector().InspectAsync(pipe));
    }

    [Fact]
    public async Task Inspect_MissingFile_ThrowsAnImageError()
    {
        var ex = await Assert.ThrowsAsync<Bootrix.Core.Errors.BootrixException>(() => new ImageInspector().InspectAsync(_dir.File("nope.iso")));

        Assert.Equal(Bootrix.Core.Errors.ErrorCode.ImageUnreadable, ex.Code);
    }

    [Fact]
    public async Task Inspect_Cancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImageInspector().InspectAsync(_dir.Write("a.img", new byte[1000]), null, cts.Token));
    }
}
