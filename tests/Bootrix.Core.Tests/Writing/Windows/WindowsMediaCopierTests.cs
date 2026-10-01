// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Wim;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class WindowsMediaCopierTests : IDisposable
{
    private readonly TestDirectory _dir = new("bootrix-copy");

    public void Dispose() => _dir.Dispose();

    private string Target => _dir.File("target");

    private string Scratch => _dir.File("scratch");

    private string WriteSourceTree(Dictionary<string, byte[]> files)
    {
        foreach (var (path, content) in files)
        {
            _dir.Write("source/" + path, content);
        }

        return _dir.File("source");
    }

    private sealed class RecordingSource(IWindowsMediaSource inner) : IWindowsMediaSource
    {
        public List<string> Opened { get; } = [];

        public IReadOnlyList<MediaSourceFile> Files => inner.Files;

        public IReadOnlyList<string> Directories => inner.Directories;

        public Stream OpenRead(string path)
        {
            Opened.Add(path);
            return inner.OpenRead(path);
        }

        public string? LocalPath(string path) => inner.LocalPath(path);

        public void Dispose() => inner.Dispose();
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public async Task CopiesEveryFileByteForByte_AndReportsTheirHashes()
    {
        var files = SetupMediaFixture.BootFiles();
        files["sources/install.wim"] = SetupMediaFixture.Random(9_000_000, 77);
        var root = WriteSourceTree(files);
        Directory.CreateDirectory(Path.Combine(root, "empty/inner"));
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create(source, new WindowsCopyOptions());

        var copied = await new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, computeHashes: true);

        foreach (var (path, content) in files)
        {
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(Target, path)));
            var record = Assert.Single(copied, file => file.Path == path);
            Assert.Equal(content.Length, record.Length);
            Assert.Equal(SHA256.HashData(content), record.Sha256);
        }

        Assert.Equal(files.Count, copied.Count);
        Assert.True(Directory.Exists(Path.Combine(Target, "empty", "inner")));
    }

    [Fact]
    public async Task WritesTheBootFilesLast()
    {
        var root = WriteSourceTree(SetupMediaFixture.BootFiles());
        using var inner = DirectoryMediaSource.Scan(root);
        var source = new RecordingSource(inner);
        var plan = WindowsCopyPlan.Create(source, new WindowsCopyOptions());

        await new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, computeHashes: false);

        Assert.Equal(plan.Items.Select(item => item.Source.Path), source.Opened);
        Assert.Equal("efi/boot/bootx64.efi", source.Opened[^1]);
        Assert.True(source.Opened.IndexOf("bootmgr") > source.Opened.IndexOf("sources/boot.wim"));
    }

    [Fact]
    public async Task WithoutHashes_TheResultIsEmptyButTheFilesAreThere()
    {
        var root = WriteSourceTree(new() { ["a.txt"] = SetupMediaFixture.Random(1000, 1) });
        using var source = DirectoryMediaSource.Scan(root);

        var copied = await new WindowsMediaCopier(source, Scratch).CopyAsync(WindowsCopyPlan.Create(source, new WindowsCopyOptions()), Target, computeHashes: false);

        Assert.Empty(copied);
        Assert.True(File.Exists(Path.Combine(Target, "a.txt")));
    }

    [Fact]
    public async Task FilesLargerThanTheBuffers_ArriveIntact()
    {
        var content = SetupMediaFixture.Random((2 * WindowsMediaCopier.BufferBytes) + 12_345, 5);
        var root = WriteSourceTree(new() { ["sources/big.bin"] = content });
        using var source = DirectoryMediaSource.Scan(root);

        var copied = await new WindowsMediaCopier(source, Scratch).CopyAsync(WindowsCopyPlan.Create(source, new WindowsCopyOptions()), Target, true);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(Target, "sources", "big.bin")));
        Assert.Equal(SHA256.HashData(content), Assert.Single(copied).Sha256);
    }

    [Fact]
    public async Task ModificationTimesSurviveTheCopy()
    {
        var root = WriteSourceTree(new() { ["a.txt"] = [1, 2, 3] });
        var stamp = new DateTime(2024, 3, 5, 10, 20, 30, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(root, "a.txt"), stamp);
        using var source = DirectoryMediaSource.Scan(root);

        await new WindowsMediaCopier(source, Scratch).CopyAsync(WindowsCopyPlan.Create(source, new WindowsCopyOptions()), Target, false);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(Target, "a.txt")));
    }

    [Fact]
    public async Task ADateBefore1980_IsLeftAloneBecauseFatCannotStoreIt()
    {
        var root = WriteSourceTree(new() { ["old.txt"] = [1, 2, 3] });
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create([new MediaSourceFile("old.txt", 3, new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))], [], new WindowsCopyOptions());

        await new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, false);

        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(Target, "old.txt")) > new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task ProgressNeverGoesBackwards_AndEndsAtTheTotal()
    {
        var files = SetupMediaFixture.BootFiles();
        files["sources/install.wim"] = SetupMediaFixture.Random(10_000_000, 78);
        var root = WriteSourceTree(files);
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create(source, new WindowsCopyOptions());
        var reports = new List<CopyProgress>();

        await new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, true, new SyncProgress<CopyProgress>(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(reports.Select(r => r.BytesDone).Order(), reports.Select(r => r.BytesDone));
        Assert.Equal(plan.TotalBytes, reports[^1].BytesDone);
        Assert.Contains(reports, report => report.File == "efi/boot/bootx64.efi");
    }

    [Fact]
    public async Task Cancellation_StopsTheCopy()
    {
        var root = WriteSourceTree(SetupMediaFixture.BootFiles());
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create(source, new WindowsCopyOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, true, null, cts.Token));
    }

    [Fact]
    public async Task ASourceThatIsShorterThanListed_IsAnError()
    {
        var root = WriteSourceTree(new() { ["a.bin"] = new byte[5000] });
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create([new MediaSourceFile("a.bin", 6000)], [], new WindowsCopyOptions());

        var ex = await Assert.ThrowsAsync<BootrixException>(() => new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, true));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [Fact]
    public async Task VolumeInternalsOfTheSource_DoNotReachTheTarget()
    {
        var root = WriteSourceTree(new() { ["setup.exe"] = [1], ["System Volume Information/x.dat"] = [2] });
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create(source.Files.Append(new MediaSourceFile("hiberfil.sys", 5)), source.Directories, new WindowsCopyOptions());

        var copied = await new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, true);

        Assert.Equal(["setup.exe"], copied.Select(file => file.Path));
        Assert.False(Directory.Exists(Path.Combine(Target, "System Volume Information")));
    }

    [Fact]
    public void DestinationPaths_UseThePlatformSeparatorAfterTheRoot()
    {
        var path = WindowsMediaCopier.DestinationPath("root/", "sources/install.swm");

        Assert.Equal("root" + Path.DirectorySeparatorChar + "sources" + Path.DirectorySeparatorChar + "install.swm", path);
        Assert.Equal("root" + Path.DirectorySeparatorChar + "a", WindowsMediaCopier.DestinationPath("root", "a"));
    }

    // --- install image splitting (real wimlib) ---------------------------------------------------------

    private const long PartBytes = 3L << 20;

    private static WindowsCopyOptions SplitOptions => new()
    {
        MaxFileBytes = 5L << 20,
        SplitInstallImage = true,
        SplitPartBytes = PartBytes,
    };

    private static void AssertSplitMedium(string target, string originalTree)
    {
        var first = Path.Combine(target, "sources", "install.swm");
        var parts = InstallImageSplitter.FindParts(first);
        Assert.True(parts.Count >= 3, $"expected several parts, got {parts.Count}");
        Assert.All(parts, part => Assert.True(new FileInfo(part).Length <= PartBytes + (128 * 1024), part));
        Assert.False(File.Exists(Path.Combine(target, "sources", "install.wim")));

        var applied = SetupMediaFixture.Apply(new TestDirectory("bootrix-apply"), first, Path.Combine(target, "sources", "install*.swm"));
        var (code, output) = WimTestTools.Run("diff", "-r", originalTree, applied);
        Assert.True(code == 0, output);
        Directory.Delete(applied, recursive: true);
    }

    [NeedsWimlibFact]
    public async Task AnInstallWimThatDoesNotFit_IsSplitStraightOntoTheTarget()
    {
        var files = SetupMediaFixture.BootFiles();
        var root = WriteSourceTree(files);
        var wim = SetupMediaFixture.CaptureWim(_dir, "install.wim", 14, 900_000);
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        File.Copy(wim, Path.Combine(root, "sources", "install.wim"));
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create(source, SplitOptions);
        Assert.True(plan.HasSplit);

        var copied = await new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, computeHashes: true);

        AssertSplitMedium(Target, _dir.File("wimtree-install.wim"));
        var parts = InstallImageSplitter.FindParts(Path.Combine(Target, "sources", "install.swm"));
        foreach (var (part, index) in parts.Select((part, index) => (part, index)))
        {
            var record = Assert.Single(copied, file => file.Path == WindowsCopyPlan.PartName("sources/install.swm", index + 1));
            Assert.Equal(new FileInfo(part).Length, record.Length);
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(part)), record.Sha256);
        }

        Assert.Equal(files.Count + parts.Count, copied.Count);
        Assert.False(Directory.Exists(Scratch) && Directory.EnumerateFileSystemEntries(Scratch).Any());
    }

    [NeedsWimlibFact]
    public async Task ASolidEsd_IsConvertedBeforeItIsSplit()
    {
        var root = WriteSourceTree(new() { ["setup.exe"] = [1] });
        var esd = SetupMediaFixture.CaptureWim(_dir, "install.esd", 14, 900_000, solid: true);
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        File.Copy(esd, Path.Combine(root, "sources", "install.esd"));
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create(source, SplitOptions);

        await new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, computeHashes: false);

        AssertSplitMedium(Target, _dir.File("wimtree-install.esd"));
        Assert.False(Directory.Exists(Scratch) && Directory.EnumerateFileSystemEntries(Scratch).Any());
    }

    [NeedsWimlibAndIsoFact]
    public async Task AnInstallWimInsideAnIsoStream_IsExtractedThenSplit()
    {
        var files = SetupMediaFixture.BootFiles();
        var wim = SetupMediaFixture.CaptureWim(_dir, "install.wim", 14, 900_000, compression: "LZX");
        files["sources/install.wim"] = File.ReadAllBytes(wim);
        var iso = IsoBuilder.Build(_dir, "setup", files);
        var inspection = await new ImageInspector().InspectAsync(iso);

        await using var stream = File.OpenRead(iso);
        using var source = IsoMediaSource.Open(stream, inspection);
        var plan = WindowsCopyPlan.Create(source, SplitOptions);
        var reports = new List<CopyProgress>();

        await new WindowsMediaCopier(source, Scratch).CopyAsync(plan, Target, true, new SyncProgress<CopyProgress>(reports.Add));

        AssertSplitMedium(Target, _dir.File("wimtree-install.wim"));
        Assert.Equal(plan.TotalBytes, reports[^1].BytesDone);
        Assert.Equal(files["bootmgr"], File.ReadAllBytes(Path.Combine(Target, "bootmgr")));
        Assert.False(Directory.Exists(Scratch) && Directory.EnumerateFileSystemEntries(Scratch).Any());
    }

    [NeedsWimlibFact]
    public async Task WhenTheDirectSplitFails_ThePartsAreCutInTheScratchFolderAndCopied()
    {
        var root = WriteSourceTree(new() { ["setup.exe"] = [1] });
        var wim = SetupMediaFixture.CaptureWim(_dir, "install.wim", 14, 900_000);
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        File.Copy(wim, Path.Combine(root, "sources", "install.wim"));
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create(source, SplitOptions);
        var attempts = new List<string>();
        var copier = new WindowsMediaCopier(source, Scratch)
        {
            Splitter = async (src, first, size, scratch, progress, token) =>
            {
                attempts.Add(first);
                if (first.StartsWith(Target, StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(first, "half a part", token);
                    throw new BootrixException(ErrorCode.ExternalToolFailed, "cannot open the target");
                }

                return await InstallImageSplitter.SplitAsync(src, first, size, scratch, progress, token);
            },
        };

        var copied = await copier.CopyAsync(plan, Target, computeHashes: true);

        Assert.Equal(2, attempts.Count);
        AssertSplitMedium(Target, _dir.File("wimtree-install.wim"));
        var parts = InstallImageSplitter.FindParts(Path.Combine(Target, "sources", "install.swm"));
        Assert.Equal(parts.Count + 1, copied.Count);
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(parts[0])), copied.Single(file => file.Path == "sources/install.swm").Sha256);
        Assert.False(Directory.Exists(Scratch) && Directory.EnumerateFileSystemEntries(Scratch).Any());
    }

    [NeedsWimlibFact]
    public async Task ACancelledSplit_LeavesNoPartsBehind()
    {
        var wim = SetupMediaFixture.CaptureWim(_dir, "install.wim", 14, 900_000);
        Directory.CreateDirectory(Target);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InstallImageSplitter.SplitAsync(wim, Path.Combine(Target, "install.swm"), PartBytes, Scratch, null, cts.Token));

        Assert.Empty(Directory.GetFiles(Target));
    }

    [NeedsWimlibFact]
    public async Task SplittingARealWim_ReportsProgressUpToOne()
    {
        var wim = SetupMediaFixture.CaptureWim(_dir, "install.wim", 14, 900_000);
        Directory.CreateDirectory(Target);
        var values = new List<double>();

        var parts = await InstallImageSplitter.SplitAsync(wim, Path.Combine(Target, "install.swm"), PartBytes, Scratch, new SyncProgress<double>(values.Add));

        Assert.True(parts.Count >= 3);
        Assert.Equal(1.0, values[^1]);
        Assert.Equal(values.Order(), values);
    }
}
