// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Wim;

namespace Bootrix.Core.Tests.Writing.Windows;

/// <summary>Builds the files of a small Windows setup medium: random content, boot files in the places setup looks for them.</summary>
internal static class SetupMediaFixture
{
    public static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new System.Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>The tree of a setup medium without the install image.</summary>
    public static Dictionary<string, byte[]> BootFiles() => new()
    {
        ["bootmgr"] = Random(40_000, 1),
        ["bootmgr.efi"] = Random(30_000, 2),
        ["boot/bcd"] = Random(16_384, 3),
        ["boot/boot.sdi"] = Random(70_000, 4),
        ["efi/boot/bootx64.efi"] = Random(50_000, 5),
        ["efi/microsoft/boot/bcd"] = Random(16_384, 6),
        ["sources/boot.wim"] = Random(200_000, 7),
        ["sources/setup.exe"] = Random(25_000, 8),
        ["setup.exe"] = Random(20_000, 9),
        ["autorun.inf"] = Random(60, 10),
        ["support/empty.txt"] = [],
    };

    /// <summary>Captures a directory of random files as a WIM with wimcapture; returns the path of the WIM.</summary>
    public static string CaptureWim(TestDirectory dir, string name, int files, int bytesPerFile, string compression = "none", bool solid = false)
    {
        var tree = dir.File("wimtree-" + name);
        Directory.CreateDirectory(tree);
        for (var i = 0; i < files; i++)
        {
            File.WriteAllBytes(Path.Combine(tree, $"file{i}.bin"), Random(bytesPerFile + i * 1000, 100 + i));
        }

        var wim = dir.File(name);
        var arguments = new List<string> { tree, wim, "Edition One" };
        arguments.Add(solid ? "--solid" : "--compress=" + compression);
        var (code, output) = WimTestTools.Run("wimcapture", [.. arguments]);
        Assert.True(code == 0, output);
        return wim;
    }

    /// <summary>Applies a (split) WIM with wimapply and returns the folder it was extracted to.</summary>
    public static string Apply(TestDirectory dir, string firstPart, string referenceGlob)
    {
        var target = dir.File("applied-" + Guid.NewGuid().ToString("N")[..6]);
        var (code, output) = WimTestTools.Run("wimapply", firstPart, "1", target, "--ref=" + referenceGlob);
        Assert.True(code == 0, output);
        return target;
    }

    public static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

/// <summary>A fact that needs wimlib with its tools and an ISO builder.</summary>
public sealed class NeedsWimlibAndIsoFactAttribute : FactAttribute
{
    public NeedsWimlibAndIsoFactAttribute()
    {
        if (!Bootrix.Core.Wim.WimFile.IsAvailable || !WimTestTools.HasTool("wimcapture") || !WimTestTools.HasTool("wimapply"))
        {
            Skip = "wimlib or its tools are not installed";
        }
        else if (!ReferenceTool.Exists("xorriso"))
        {
            Skip = "xorriso is not installed";
        }
    }
}
