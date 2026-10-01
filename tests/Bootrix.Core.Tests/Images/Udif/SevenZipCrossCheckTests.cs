// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Images.Udif;
using Xunit.Abstractions;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>
/// 7-Zip reads UDIF independently of this code base. Every image written by the test builder is extracted
/// with it and compared with the source volume and with the output of <see cref="DmgReader"/>.
/// </summary>
public sealed partial class SevenZipCrossCheckTests : IDisposable
{
    private const string SevenZipNames = "7z|7zz|7za";

    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-7z-" + Guid.NewGuid().ToString("N"));

    public SevenZipCrossCheckTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [RequiresToolFact(SevenZipNames)]
    public void ExtractedVolume_ForEveryCodec_EqualsSourceAndReaderOutput()
    {
        foreach (var codec in new[] { "raw", "zlib", "bzip2", "adc", "mixed" })
        {
            var path = Write(codec + ".dmg", DmgFixtures.Build(codec));

            var extracted = ExtractVolume(path);

            Assert.True(DmgFixtures.Volume.AsSpan().SequenceEqual(extracted), $"7-Zip output differs for {codec}");
            using var reader = DmgReader.Open(path);
            Assert.Equal(extracted, DmgFixtures.ReadAll(reader));
        }
    }

    [RequiresToolFact(SevenZipNames)]
    public void ExtractedVolume_ForLayoutVariants_EqualsSourceAndReaderOutput()
    {
        var variants = new (string Name, UdifBuilder Template)[]
        {
            ("front", new UdifBuilder { TrailerAtFront = true }),
            ("front-offset", new UdifBuilder { TrailerAtFront = true, Prefix = 128 }),
            ("resource-fork", new UdifBuilder { ResourceFork = true }),
            ("comments", new UdifBuilder { Comments = true }),
            ("chunk-size", new UdifBuilder()),
        };

        foreach (var (name, template) in variants)
        {
            var chunkSectors = name == "chunk-size" ? 1 : 64;
            var path = Write(name + ".dmg", DmgFixtures.Build("mixed", chunkSectors, template));

            var extracted = ExtractVolume(path);

            Assert.True(DmgFixtures.Volume.AsSpan().SequenceEqual(extracted), $"7-Zip output differs for {name}");
            using var reader = DmgReader.Open(path);
            Assert.Equal(extracted, DmgFixtures.ReadAll(reader));
        }
    }

    [RequiresToolFact(SevenZipNames)]
    public void ReferenceEncoderChunks_LzfseAndXz_AreReadIdenticallyBy7Zip()
    {
        var chunks = CodecFixtures.Lzfse.Concat(CodecFixtures.Xz).Select(f => f.ToChunk()).ToList();
        chunks.Insert(2, ChunkSpec.Zero(16));
        var expected = new MemoryStream();
        foreach (var chunk in chunks)
        {
            expected.Write(chunk.Plain ?? new byte[chunk.SectorCount * 512]);
        }

        var path = Write("reference.dmg", new UdifBuilder().AddPartition(0, "disk image (Apple_HFS : 1)", 0, chunks).Build());

        var (exitCode, output) = ExternalTool.Run(ExternalTool.Find(SevenZipNames)!, ["t", "-tdmg", path]);
        _output.WriteLine(output);
        if (output.Contains("Unsupported Method", StringComparison.OrdinalIgnoreCase))
        {
            // Older 7-Zip builds do not know LZFSE or XZ chunks; the other codecs are covered above.
            _output.WriteLine("installed 7-Zip lacks LZFSE/XZ support");
            return;
        }

        Assert.Equal(0, exitCode);
        Assert.True(expected.ToArray().AsSpan().SequenceEqual(ExtractVolume(path)));
        using var reader = DmgReader.Open(path);
        Assert.Equal(expected.ToArray(), DmgFixtures.ReadAll(reader));
    }

    [RequiresToolFact(SevenZipNames)]
    public void ChecksumsWrittenByTheBuilder_AreAcceptedBy7Zip_AndRejectedWhenWrong()
    {
        var valid = Write("valid.dmg", DmgFixtures.Build("zlib"));
        var (validExit, validOutput) = ExternalTool.Run(ExternalTool.Find(SevenZipNames)!, ["t", "-tdmg", valid]);
        _output.WriteLine(validOutput);
        Assert.Equal(0, validExit);
        Assert.Contains("Everything is Ok", validOutput, StringComparison.Ordinal);

        var wrongData = DmgFixtures.Build("raw");
        wrongData[40 * 512 + 3000] ^= 0xFF;
        var wrongPath = Write("wrong-data.dmg", wrongData);
        var (wrongExit, wrongOutput) = ExternalTool.Run(ExternalTool.Find(SevenZipNames)!, ["t", "-tdmg", wrongPath]);
        _output.WriteLine(wrongOutput);
        Assert.NotEqual(0, wrongExit);

        var wrongMaster = Write("wrong-master.dmg", DmgFixtures.Build("zlib", template: new UdifBuilder
        {
            MutateTrailer = trailer => trailer[0x160 + 8 + 3] ^= 0x01,
        }));
        var (masterExit, masterOutput) = ExternalTool.Run(ExternalTool.Find(SevenZipNames)!, ["t", "-tdmg", wrongMaster]);
        _output.WriteLine(masterOutput);
        Assert.True(masterExit != 0 || masterOutput.Contains("Warning", StringComparison.OrdinalIgnoreCase));
    }

    private string Write(string name, byte[] image)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, image);
        return path;
    }

    // Each partition comes out as "<index>.<type>"; concatenated in index order they are the volume.
    private byte[] ExtractVolume(string dmg)
    {
        var target = Path.Combine(_directory, "out-" + Guid.NewGuid().ToString("N"));
        var (exitCode, output) = ExternalTool.Run(ExternalTool.Find(SevenZipNames)!, ["x", "-y", "-tdmg", "-o" + target, dmg]);
        _output.WriteLine(output);
        Assert.Equal(0, exitCode);

        using var volume = new MemoryStream();
        foreach (var file in Directory.GetFiles(target).OrderBy(f => int.Parse(IndexPrefix().Match(Path.GetFileName(f)).Value, System.Globalization.CultureInfo.InvariantCulture)))
        {
            volume.Write(File.ReadAllBytes(file));
        }

        return volume.ToArray();
    }

    [GeneratedRegex(@"^\d+")]
    private static partial Regex IndexPrefix();
}
