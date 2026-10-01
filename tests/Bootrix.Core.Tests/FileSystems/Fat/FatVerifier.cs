// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.FileSystems.Fat;

/// <summary>Checks a finished image with the tools that ship with Linux, independent of the formatter's own reader.</summary>
internal static partial class FatVerifier
{
    public static FsckReport Fsck(string imagePath)
    {
        var result = ExternalTools.Run("fsck.vfat", "-n", "-v", imagePath);
        Assert.True(result.ExitCode == 0, $"fsck.vfat reported problems (exit {result.ExitCode}):\n{result.Combined}");

        var clusters = DataClusters().Match(result.Output);
        Assert.True(clusters.Success, $"no cluster count in fsck output:\n{result.Output}");
        return new FsckReport(long.Parse(clusters.Groups[1].Value, CultureInfo.InvariantCulture), result.Output);
    }

    /// <summary>Copies a file in, lists the directory, copies it back out and compares.</summary>
    public static void MtoolsRoundTrip(string imagePath, long offset = 0, int payloadBytes = 300_000)
    {
        var image = offset == 0 ? imagePath : $"{imagePath}@@{offset}";
        var directory = Path.Combine(Path.GetTempPath(), $"bootrix-mtools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "payload.bin");
            var payload = new byte[payloadBytes];
            new Random(42).NextBytes(payload);
            File.WriteAllBytes(source, payload);

            var copyIn = ExternalTools.Run("mcopy", "-i", image, source, "::PAYLOAD.BIN");
            Assert.True(copyIn.ExitCode == 0, $"mcopy in failed:\n{copyIn.Combined}");

            var listing = ExternalTools.Run("mdir", "-i", image, "::");
            Assert.True(listing.ExitCode == 0, $"mdir failed:\n{listing.Combined}");
            Assert.Contains("PAYLOAD", listing.Output, StringComparison.Ordinal);

            var target = Path.Combine(directory, "copy.bin");
            var copyOut = ExternalTools.Run("mcopy", "-i", image, "::PAYLOAD.BIN", target);
            Assert.True(copyOut.ExitCode == 0, $"mcopy out failed:\n{copyOut.Combined}");
            Assert.Equal(payload, File.ReadAllBytes(target));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static string MtoolsLabel(string imagePath)
    {
        var result = ExternalTools.Run("mlabel", "-i", imagePath, "-s", "::");
        Assert.True(result.ExitCode == 0, $"mlabel failed:\n{result.Combined}");
        var line = result.Output.Trim();
        return line.StartsWith("Volume label is ", StringComparison.Ordinal) ? line["Volume label is ".Length..].Trim() : "";
    }

    /// <summary>The boot sector fields mtools reads back, keyed by its own labels ("sector size", "disk label", ...).</summary>
    public static Dictionary<string, string> Minfo(string imagePath)
    {
        var result = ExternalTools.Run("minfo", "-i", imagePath, "::");
        Assert.True(result.ExitCode == 0, $"minfo failed:\n{result.Combined}");

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n'))
        {
            var separator = line.IndexOfAny([':', '=']);
            if (separator > 0 && !line.StartsWith("mformat", StringComparison.Ordinal))
            {
                fields[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"').Trim();
            }
        }

        return fields;
    }

    /// <summary>DiscUtils parses the BPB on its own; it has to find the same type and geometry and be able to write a file.</summary>
    public static void DiscUtilsAgrees(string imagePath, FatLayout layout)
    {
        using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        using var fs = new DiscUtils.Fat.FatFileSystem(stream);

        Assert.Equal((int)layout.Type, (int)fs.FatVariant);
        Assert.Equal(layout.SectorsPerCluster, fs.SectorsPerCluster);
        Assert.Equal(layout.BytesPerSector, fs.SectorSize);
        Assert.Equal(layout.ReservedSectors, fs.ReservedSectorCount);

        using (var file = fs.OpenFile("DISCUTIL.TXT", FileMode.Create))
        {
            file.Write("written by DiscUtils"u8);
        }

        Assert.True(fs.FileExists("DISCUTIL.TXT"));
    }

    [GeneratedRegex(@"(\d+) data clusters")]
    private static partial Regex DataClusters();
}

internal sealed record FsckReport(long DataClusters, string Output);
