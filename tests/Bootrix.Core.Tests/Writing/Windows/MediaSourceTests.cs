// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class MediaSourceTests : IDisposable
{
    private readonly TestDirectory _dir = new("bootrix-source");

    public void Dispose() => _dir.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static Dictionary<string, byte[]> SampleTree() => new()
    {
        ["bootmgr"] = Bytes("boot manager"),
        ["efi/boot/bootx64.efi"] = Bytes("loader"),
        ["sources/install.wim"] = new byte[300_000],
        ["sources/$OEM$/readme.txt"] = Bytes("oem"),
        ["Support/Tools/x.exe"] = Bytes("x"),
    };

    [Fact]
    public void DirectorySource_ListsFilesAndEmptyDirectories_WithSlashes()
    {
        foreach (var (path, content) in SampleTree())
        {
            _dir.Write("tree/" + path, content);
        }

        Directory.CreateDirectory(_dir.File("tree/empty/inner"));

        using var source = DirectoryMediaSource.Scan(_dir.File("tree"));

        Assert.Equal(
            ["Support/Tools/x.exe", "bootmgr", "efi/boot/bootx64.efi", "sources/$OEM$/readme.txt", "sources/install.wim"],
            source.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Contains("empty/inner", source.Directories);
        Assert.Contains("sources/$OEM$", source.Directories);
        Assert.Equal(300_000, source.Files.Single(file => file.Name == "install.wim").Length);
        Assert.All(source.Files, file => Assert.NotNull(file.LastWriteUtc));
    }

    [Fact]
    public void DirectorySource_IncludesHiddenFiles()
    {
        // On Linux a leading dot is the hidden attribute; on Windows bootmgr itself is hidden and system.
        _dir.Write("tree/.hidden.txt", Bytes("h"));
        _dir.Write("tree/visible.txt", Bytes("v"));
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(_dir.File("tree/visible.txt"), FileAttributes.Hidden | FileAttributes.System);
        }

        using var source = DirectoryMediaSource.Scan(_dir.File("tree"));

        Assert.Equal([".hidden.txt", "visible.txt"], source.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DirectorySource_SkipsVolumeInternals_WithoutEnteringThem()
    {
        _dir.Write("tree/setup.exe", Bytes("s"));
        _dir.Write("tree/System Volume Information/tracking.log", Bytes("t"));
        _dir.Write("tree/$RECYCLE.BIN/x", Bytes("x"));

        using var source = DirectoryMediaSource.Scan(_dir.File("tree"));

        Assert.Equal(["setup.exe"], source.Files.Select(file => file.Path));
        Assert.Empty(source.Directories);
    }

    [Fact]
    public async Task DirectorySource_OpensFilesByTheirRelativePath()
    {
        _dir.Write("tree/sources/boot.wim", Bytes("wim data"));
        using var source = DirectoryMediaSource.Scan(_dir.File("tree"));

        await using var stream = source.OpenRead("sources/boot.wim");
        using var reader = new StreamReader(stream);

        Assert.Equal("wim data", await reader.ReadToEndAsync());
        Assert.Equal(_dir.File("tree/sources/boot.wim"), source.LocalPath("sources/boot.wim"));
        Assert.Throws<FileNotFoundException>(() => source.OpenRead("sources/missing.wim"));
    }

    [Fact]
    public void DirectorySource_DoesNotFollowDirectoryLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        _dir.Write("tree/a.txt", Bytes("a"));
        Directory.CreateSymbolicLink(_dir.File("tree/loop"), _dir.File("tree"));

        using var source = DirectoryMediaSource.Scan(_dir.File("tree"));

        Assert.Equal(["a.txt"], source.Files.Select(file => file.Path));
        Assert.Empty(source.Directories);
    }

    [ToolFact("xorriso")]
    public async Task IsoSource_ReadsTheSameTreeAsTheDirectoryItWasBuiltFrom()
    {
        var tree = SampleTree();
        tree["empty-dir/keep.txt"] = Bytes("k");
        var iso = IsoBuilder.Build(_dir, "setup", tree);
        var inspection = await new ImageInspector().InspectAsync(iso);

        await using var stream = File.OpenRead(iso);
        using var source = IsoMediaSource.Open(stream, inspection);

        Assert.Equal(
            tree.Keys.Order(StringComparer.OrdinalIgnoreCase),
            source.Files.Select(file => file.Path).Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains("efi/boot", source.Directories, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("sources/$OEM$", source.Directories, StringComparer.OrdinalIgnoreCase);

        using var content = source.OpenRead("bootmgr");
        using var reader = new StreamReader(content);
        Assert.Equal("boot manager", await reader.ReadToEndAsync());
        Assert.Null(source.LocalPath("bootmgr"));
        Assert.Equal(300_000, source.Files.Single(file => file.Name == "install.wim").Length);
    }

    [ToolFact("xorriso")]
    public async Task IsoSource_Refuses_AFileThatIsNotPartOfTheImage()
    {
        var iso = IsoBuilder.Build(_dir, "small", new Dictionary<string, byte[]> { ["a.txt"] = Bytes("a") });
        var inspection = await new ImageInspector().InspectAsync(iso);

        await using var stream = File.OpenRead(iso);
        using var source = IsoMediaSource.Open(stream, inspection);

        Assert.Throws<FileNotFoundException>(() => source.OpenRead("b.txt"));
    }

    [Fact]
    public async Task IsoSource_Refuses_AStreamThatIsNoIso()
    {
        var path = _dir.Write("noise.bin", new byte[100_000]);
        var inspection = await new ImageInspector().InspectAsync(path);

        await using var stream = File.OpenRead(path);
        var ex = Assert.Throws<BootrixException>(() => IsoMediaSource.Open(stream, inspection));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }
}
