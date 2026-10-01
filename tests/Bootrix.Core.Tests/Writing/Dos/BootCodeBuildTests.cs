// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Writing.Dos;

/// <summary>
/// The boot sectors in assets/third-party/freedos are built from the FreeDOS sources in tools/bootcode with nasm. These tests rebuild
/// them and compare, and check that the files of the assets folder are those its checksum list names; the same checks tools/bootcode/build.sh --check makes.
/// </summary>
public class BootCodeBuildTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Assets = Path.Combine(Root, "assets", "third-party", "freedos");
    private static readonly string Sources = Path.Combine(Root, "tools", "bootcode", "freedos");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bootrix.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root with Bootrix.slnx was not found above the test assembly.");
    }

    private static Dictionary<string, string> Checksums(string folder) =>
        File.ReadAllLines(Path.Combine(folder, "SHA256SUMS"))
            .Where(line => line.Length > 66)
            .ToDictionary(line => line[66..].Trim(), line => line[..64], StringComparer.Ordinal);

    [Fact]
    public void EveryFileOfTheAssetsFolder_HasTheHashOfItsChecksumList()
    {
        var sums = Checksums(Assets);

        Assert.Equal(10, sums.Count);
        foreach (var (name, hash) in sums)
        {
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Assets, name)))));
        }
    }

    [Fact]
    public void TheSources_AreTheUnchangedFilesOfTheirChecksumList()
    {
        var sums = Checksums(Sources);

        Assert.Equal(["COPYING", "boot.asm", "boot32lb.asm"], sums.Keys.Order(StringComparer.Ordinal));
        foreach (var (name, hash) in sums)
        {
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Sources, name)))));
        }
    }

    [RequiresToolTheory("nasm")]
    [InlineData("fat12com.bin", "boot.asm", "-dISFAT12")]
    [InlineData("fat16com.bin", "boot.asm", "-dISFAT16")]
    [InlineData("fat32lba.bin", "boot32lb.asm", null)]
    public void RebuildingTheBootSector_GivesTheCheckedInBytes_AndTheOnesTheProgramEmbeds(string asset, string source, string? define)
    {
        var output = Path.Combine(Path.GetTempPath(), $"bootrix-nasm-{Guid.NewGuid():N}.bin");
        try
        {
            string[] arguments = define is null
                ? [Path.Combine(Sources, source), "-o", output]
                : [define, Path.Combine(Sources, source), "-o", output];

            var result = ExternalTools.Run("nasm", arguments);

            Assert.True(result.ExitCode == 0, result.Combined);
            var built = File.ReadAllBytes(output);
            Assert.Equal(512, built.Length);
            Assert.Equal(File.ReadAllBytes(Path.Combine(Assets, asset)), built);
            Assert.Equal(DosAssets.FreeDos(asset), built);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
