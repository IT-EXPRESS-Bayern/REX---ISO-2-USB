// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot;
using DiscUtils.Fat;

namespace Bootrix.Core.Tests.Boot;

/// <summary>
/// The analyzer takes streams, the caller enumerates the files. This is the intended use with a real file system:
/// a FAT image such as the EFI system partition or the El Torito EFI image of an ISO.
/// </summary>
public class EfiMediaFromFileSystemTests(AuthorityFixture authorities) : IClassFixture<AuthorityFixture>
{
    private static MemoryStream BuildEfiPartition(params (string Path, byte[] Content)[] files)
    {
        var disk = new MemoryStream();
        using (var fs = FatFileSystem.FormatFloppy(disk, DiscUtils.FloppyDiskType.HighDensity, "EFISYS"))
        {
            foreach (var (path, content) in files)
            {
                var directory = path[..path.LastIndexOf('\\')];
                fs.CreateDirectory(directory);
                using var target = fs.OpenFile(path, FileMode.Create);
                target.Write(content);
            }
        }

        disk.Position = 0;
        return disk;
    }

    private static EfiAnalysisReport AnalyzeEveryEfiFile(Stream disk)
    {
        using var fs = new FatFileSystem(disk);
        var streams = fs.GetFiles(@"\", "*.EFI", SearchOption.AllDirectories)
            .Select(path => (Path: path, Data: (Stream)fs.OpenFile(path, FileMode.Open, FileAccess.Read)))
            .ToList();
        try
        {
            return new EfiMediaAnalyzer().Analyze(streams);
        }
        finally
        {
            streams.ForEach(s => s.Data.Dispose());
        }
    }

    [Fact]
    public void EfiPartitionOfAFatImage_IsAnalysedFileByFile()
    {
        var unsigned = PeBuilder.Typical().Build();
        var image = Signed(PeBuilder.Typical);
        using var disk = BuildEfiPartition(
            (@"EFI\BOOT\BOOTX64.EFI", image),
            (@"EFI\BOOT\BOOTAA64.EFI", new PeBuilder { Machine = 0xAA64 }.AddSection(".text", [1, 2, 3]).Build()),
            (@"EFI\tools\shell.efi", unsigned),
            (@"EFI\BOOT\readme.txt", [1, 2, 3]));

        var report = AnalyzeEveryEfiFile(disk);

        Assert.Equal(3, report.Files.Count);
        var x64 = report.Files.Single(f => f.Path.EndsWith("BOOTX64.EFI", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(EfiFileRole.FallbackLoader, x64.Role);
        Assert.Equal(EfiMachine.X64, x64.Machine);
        Assert.Equal([SignatureAuthority.Other], x64.Authorities);
        Assert.Equal(EfiMachine.Arm64, report.Files.Single(f => f.Path.EndsWith("BOOTAA64.EFI", StringComparison.OrdinalIgnoreCase)).Machine);
        Assert.Equal(EfiFileRole.Other, report.Files.Single(f => f.Path.EndsWith("shell.efi", StringComparison.OrdinalIgnoreCase)).Role);
        Assert.Equal([EfiMachine.X64, EfiMachine.Arm64], report.Matrix.Cells.Select(c => c.Machine).Distinct().Order());
    }

    [Fact]
    public void HashOfAFileInTheImage_EqualsTheHashOfTheOriginalBytes()
    {
        var image = Signed(PeBuilder.Typical);
        using var disk = BuildEfiPartition((@"EFI\BOOT\BOOTX64.EFI", image));

        var report = AnalyzeEveryEfiFile(disk);

        var expected = Convert.ToHexString(EfiBinary.Parse(image).ComputeAuthenticodeHash(System.Security.Cryptography.HashAlgorithmName.SHA256));
        Assert.Equal(expected, Assert.Single(report.Files).AuthenticodeSha256);
        Assert.Equal(image.Length, Assert.Single(report.Files).Size);
    }

    private byte[] Signed(Func<PeBuilder> make)
    {
        var block = authorities.Own.Sign(make().Build());
        return make().AddCertificate(block).Build();
    }
}
