// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Dism;

namespace Bootrix.Windows.Tests.Dism;

public sealed class ImageFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootrix-ifs-" + Guid.NewGuid().ToString("N"));
    private readonly ImageFileSystem _fs = new();

    public ImageFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private string Make(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task DeleteRemovesReadOnlyFilesAndFolders()
    {
        var file = Make(Path.Combine("Edge", "app", "msedge.dll"));
        File.SetAttributes(file, FileAttributes.ReadOnly);

        await _fs.DeleteAsync(Path.Combine(_root, "Edge"), takeOwnership: false, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(_root, "Edge")));
    }

    [Fact]
    public async Task DeleteOfAMissingPathIsNotAnError()
    {
        await _fs.DeleteAsync(Path.Combine(_root, "nothing"), takeOwnership: true, CancellationToken.None);
    }

    [Fact]
    public async Task WildcardsExpandInsideTheParentFolder()
    {
        Make(Path.Combine("sxs", "amd64_microsoft-edge-webview_31bf_1.0", "a.dll"));
        Make(Path.Combine("sxs", "amd64_microsoft-edge-webview_31bf_2.0", "b.dll"));
        Make(Path.Combine("sxs", "amd64_other_1.0", "c.dll"));

        await _fs.DeleteAsync(Path.Combine(_root, "sxs", "amd64_microsoft-edge-webview_31bf*"), takeOwnership: true, CancellationToken.None);

        Assert.Equal(["amd64_other_1.0"], Directory.GetDirectories(Path.Combine(_root, "sxs")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task WinSxsRebuildKeepsOnlyWhatMatchesThePatterns()
    {
        var sxs = Path.Combine(_root, "WinSxS");
        Make(Path.Combine("WinSxS", "Manifests", "a.manifest"));
        Make(Path.Combine("WinSxS", "Catalogs", "a.cat"));
        Make(Path.Combine("WinSxS", "amd64_microsoft-windows-servicingstack_31bf3856ad364e35_10.0.1", "stack.dll"));
        Make(Path.Combine("WinSxS", "amd64_microsoft-windows-shell32_31bf3856ad364e35_10.0.1", "shell.dll"));
        Make(Path.Combine("WinSxS", "x86_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_6.0", "p.dll"));
        File.SetAttributes(Path.Combine(sxs, "Catalogs", "a.cat"), FileAttributes.ReadOnly);

        await _fs.RebuildWinSxsAsync(
            sxs,
            ["Manifests", "Catalogs", "amd64_microsoft-windows-servicingstack_31bf3856ad364e35_*", "x86_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_*"],
            CancellationToken.None);

        var names = Directory.GetDirectories(sxs).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            [
                "Catalogs",
                "Manifests",
                "amd64_microsoft-windows-servicingstack_31bf3856ad364e35_10.0.1",
                "x86_microsoft.windows.i..utomation.proxystub_6595b64144ccf1df_6.0",
            ],
            names);
        Assert.True(File.Exists(Path.Combine(sxs, "Catalogs", "a.cat")));
        Assert.False(Directory.Exists(sxs + "_edit"));
    }

    [Fact]
    public async Task EmptyFileReplacesTheOriginal()
    {
        var path = Make(Path.Combine("Windows", "System32", "Recovery", "winre.wim"), "big recovery image");

        await _fs.ReplaceWithEmptyFileAsync(path, CancellationToken.None);

        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public async Task CopyAppliesTheFilterAndMakesFilesWritable()
    {
        var source = Path.Combine(_root, "iso");
        Make(Path.Combine("iso", "sources", "boot.wim"), "boot");
        Make(Path.Combine("iso", "sources", "install.esd"), "install");
        Make(Path.Combine("iso", "bootmgr"), "bootmgr");
        File.SetAttributes(Path.Combine(source, "bootmgr"), FileAttributes.ReadOnly);
        var destination = Path.Combine(_root, "media");
        var progress = new List<double>();

        await _fs.CopyDirectoryAsync(source, destination, f => !f.EndsWith("install.esd", StringComparison.Ordinal), new SyncProgress(progress.Add), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(destination, "sources", "boot.wim")));
        Assert.True(File.Exists(Path.Combine(destination, "bootmgr")));
        Assert.False(File.Exists(Path.Combine(destination, "sources", "install.esd")));
        Assert.False(File.GetAttributes(Path.Combine(destination, "bootmgr")).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public async Task CopyHonoursCancellation()
    {
        Make(Path.Combine("src", "a.bin"), new string('a', 1000));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _fs.CopyDirectoryAsync(Path.Combine(_root, "src"), Path.Combine(_root, "dst"), null, null, cts.Token));
    }

    [Fact]
    public void FreeBytesAreReported()
    {
        Assert.True(_fs.GetFreeBytes(_root) > 0);
    }

    private sealed class SyncProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
