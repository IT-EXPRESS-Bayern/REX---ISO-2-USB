// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Tests.Net.Support;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

/// <summary>
/// The hand-built cabinets of <see cref="CabinetArchiveTests"/> are also unpacked by 7-Zip, so that the layout they
/// assume (reserved areas, MSZIP history, call translation) is the one an independent reader finds.
/// </summary>
public class CabinetSevenZipTests
{
    private static Dictionary<string, byte[]> SevenZipExtract(byte[] cabinet)
    {
        using var directory = new TempDirectory();
        File.WriteAllBytes(directory.File("test.cab"), cabinet);

        var result = ExternalTools.Run("7z", "x", "-y", $"-o{directory.Path}", directory.File("test.cab"));

        Assert.True(result.ExitCode == 0, "7-Zip rejected the cabinet: " + result.Combined);
        return Directory.GetFiles(directory.Path)
            .Where(f => Path.GetFileName(f) != "test.cab")
            .ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
    }

    [RequiresToolFact("7z")]
    public void StoredFolders_WithReservedAreasAndASignatureBehindTheData_ReadTheSame()
    {
        // One block per folder: 7-Zip, like the format's tools, assumes 32 KiB blocks and allows only the last to be shorter.
        var cabinet = new CabinetBuilder { HeaderReserve = 20, FolderReserve = 3, DataReserve = 2, Trailer = new byte[100] }
            .Folder(CabinetBuilder.Stored, ("signed cabinet"u8.ToArray(), 14))
            .Folder(CabinetBuilder.Stored, ("also"u8.ToArray(), 4))
            .File("a.txt", 0, 0, 14)
            .File("b.txt", 1, 0, 4)
            .Build();

        var files = SevenZipExtract(cabinet);
        var ours = CabinetArchive.Parse(cabinet);

        Assert.Equal("signed cabinet", Encoding.ASCII.GetString(files["a.txt"]));
        Assert.Equal("also", Encoding.ASCII.GetString(files["b.txt"]));
        Assert.Equal(files["a.txt"], ours.Extract(ours.Entries[0]));
        Assert.Equal(files["b.txt"], ours.Extract(ours.Entries[1]));
    }

    [RequiresToolFact("7z")]
    public void MsZipBlockThatRefersIntoThePreviousBlock_ReadsTheSame()
    {
        // MSZIP blocks are 32 KiB except the last, so that is what a second block can refer back into.
        var first = Enumerable.Repeat("0123456789ABCDEF"u8.ToArray(), 2048).SelectMany(x => x).ToArray();
        var cabinet = new CabinetBuilder()
            .Folder(CabinetBuilder.MsZip, (CabinetArchiveTests.MsZipBlock(first), first.Length), (CabinetArchiveTests.MsZipBlockWithMatch((byte)'Z', 8, 8), 9))
            .File("f.txt", 0, 0, (uint)first.Length + 9)
            .Build();

        var theirs = SevenZipExtract(cabinet)["f.txt"];
        var ours = CabinetArchive.Parse(cabinet);

        Assert.EndsWith("DEFZ9ABCDEFZ", Encoding.ASCII.GetString(theirs), StringComparison.Ordinal);
        Assert.Equal(theirs, ours.Extract(ours.Entries[0]));
    }

    [RequiresToolFact("7z")]
    public void LzxCallTranslation_RestoresTheSameOperands()
    {
        const int fileSize = 65536;
        var data = new byte[100];
        foreach (var (at, target) in new[] { (20, 1000), (40, -5), (60, fileSize + 7) })
        {
            data[at] = 0xE8;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at + 1), target);
        }

        data[95] = 0xE8;

        var frame = new LzxFrameWriter().StreamHeader(fileSize).UncompressedBlock(100).Raw(data).ToArray();
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Lzx(15), (frame, 100)).File("f.bin", 0, 0, 100).Build();

        var archive = CabinetArchive.Parse(cabinet);

        Assert.Equal(SevenZipExtract(cabinet)["f.bin"], archive.Extract(archive.Entries[0]));
    }

    [RequiresToolFact("7z")]
    public void LzxCallTranslation_AcrossFramesAndWithCodedBlocks_RestoresTheSameOperands()
    {
        // Operands in the second frame are translated with an offset of 32 768 added to their position.
        var data = new byte[40_000];
        new Random(5).NextBytes(data);
        for (var at = 100; at < data.Length - 10; at += 977)
        {
            data[at] = 0xE8;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(at + 1), at * 3 % 50_000);
        }

        var first = new LzxFrameWriter().StreamHeader(50_000).UncompressedBlock(data.Length).Raw(data.AsSpan(..32_768)).ToArray();
        var second = new LzxFrameWriter().Raw(data.AsSpan(32_768..)).ToArray();
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Lzx(16), (first, 32_768), (second, data.Length - 32_768)).File("f.bin", 0, 0, (uint)data.Length).Build();

        var archive = CabinetArchive.Parse(cabinet);
        var ours = archive.Extract(archive.Entries[0]);

        Assert.Equal(SevenZipExtract(cabinet)["f.bin"], ours);
        Assert.NotEqual(data, ours);
    }
}
