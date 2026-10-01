// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Wim;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class MediaVerifierTests : IDisposable
{
    private readonly TestDirectory _dir = new("bootrix-verify");

    public void Dispose() => _dir.Dispose();

    private string Target => _dir.File("target");

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private async Task<IReadOnlyList<CopiedFile>> CopyBootFiles()
    {
        foreach (var (path, content) in SetupMediaFixture.BootFiles())
        {
            _dir.Write("source/" + path, content);
        }

        using var source = DirectoryMediaSource.Scan(_dir.File("source"));
        return await new WindowsMediaCopier(source, _dir.File("scratch")).CopyAsync(WindowsCopyPlan.Create(source, new WindowsCopyOptions()), Target, true);
    }

    [Fact]
    public async Task AnIntactCopy_Passes_AndCountsWhatItRead()
    {
        var copied = await CopyBootFiles();
        var reports = new List<long>();

        var result = await MediaVerifier.VerifyAsync(Target, copied, new SyncProgress<long>(reports.Add));

        Assert.Equal(copied.Count, result.Files);
        Assert.Equal(copied.Sum(file => file.Length), result.Bytes);
        Assert.Equal(result.Bytes, reports[^1]);
    }

    [Fact]
    public async Task OneFlippedBit_IsFoundAndNamesTheFile()
    {
        var copied = await CopyBootFiles();
        var path = Path.Combine(Target, "sources", "boot.wim");
        var content = File.ReadAllBytes(path);
        content[content.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, content);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => MediaVerifier.VerifyAsync(Target, copied));

        Assert.Equal(ErrorCode.MediaFileMismatch, ex.Code);
        Assert.Equal(["sources/boot.wim"], ex.Arguments);
    }

    [Fact]
    public async Task AMissingFile_IsReported()
    {
        var copied = await CopyBootFiles();
        File.Delete(Path.Combine(Target, "bootmgr"));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => MediaVerifier.VerifyAsync(Target, copied));

        Assert.Equal(["bootmgr"], ex.Arguments);
    }

    [Fact]
    public async Task ATruncatedFile_IsReported()
    {
        var copied = await CopyBootFiles();
        var path = Path.Combine(Target, "boot", "boot.sdi");
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..1000]);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => MediaVerifier.VerifyAsync(Target, copied));

        Assert.Equal(["boot/boot.sdi"], ex.Arguments);
    }

    [Fact]
    public async Task Cancellation_StopsTheVerification()
    {
        var copied = await CopyBootFiles();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MediaVerifier.VerifyAsync(Target, copied, null, cts.Token));
    }

    // --- split sets (real wimlib output) ---------------------------------------------------------------

    private async Task<(IReadOnlyList<CopiedFile> Written, string Root)> SplitMedium(string name = "install.wim")
    {
        var root = _dir.File("source-" + name);
        Directory.CreateDirectory(Path.Combine(root, "sources"));
        File.Copy(SetupMediaFixture.CaptureWim(_dir, name, 14, 900_000), Path.Combine(root, "sources", "install.wim"));
        using var source = DirectoryMediaSource.Scan(root);
        var plan = WindowsCopyPlan.Create(source, new WindowsCopyOptions { MaxFileBytes = 5L << 20, SplitInstallImage = true, SplitPartBytes = 3L << 20 });
        var target = _dir.File("target-" + name);
        return (await new WindowsMediaCopier(source, _dir.File("scratch")).CopyAsync(plan, target, true), target);
    }

    [NeedsWimlibFact]
    public async Task ASplitImage_PassesHashAndStructureChecks()
    {
        var (written, root) = await SplitMedium();

        var result = await MediaVerifier.VerifyAsync(root, written);

        Assert.True(result.Files >= 3);
        Assert.All(written, part => Assert.Equal(new FileInfo(Path.Combine(root, "sources", Path.GetFileName(part.Path))).Length, part.Length));
    }

    [NeedsWimlibFact]
    public async Task EachPartIsExactlyAsLongAsItsHeaderDescribes()
    {
        var (written, root) = await SplitMedium();

        foreach (var part in written)
        {
            var path = Path.Combine(root, "sources", Path.GetFileName(part.Path));
            var header = Bootrix.Core.Images.Wim.WimHeader.Parse(File.ReadAllBytes(path).AsSpan(0, Bootrix.Core.Images.Wim.WimHeader.Size));
            Assert.Equal(new FileInfo(path).Length, header.ExpectedLength);
        }
    }

    [NeedsWimlibFact]
    public async Task ASplitSetWithAMissingPart_IsInvalid()
    {
        var (written, root) = await SplitMedium();

        var ex = Assert.Throws<BootrixException>(() => MediaVerifier.CheckSplitSet(root, "sources/install.swm", [.. written.Where(file => file.Path != "sources/install4.swm" && !file.Path.StartsWith("sources/install5", StringComparison.Ordinal))]));

        Assert.Equal(ErrorCode.SplitSetInvalid, ex.Code);
    }

    [NeedsWimlibFact]
    public async Task APartOfAnotherImage_IsInvalid()
    {
        var (written, root) = await SplitMedium();
        var (_, otherRoot) = await SplitMedium("other.wim");
        File.Copy(Path.Combine(otherRoot, "sources", "install2.swm"), Path.Combine(root, "sources", "install2.swm"), overwrite: true);

        var ex = Assert.Throws<BootrixException>(() => MediaVerifier.CheckSplitSet(root, "sources/install.swm", written));

        Assert.Equal(ErrorCode.SplitSetInvalid, ex.Code);
        Assert.Contains("another image", ex.Message, StringComparison.Ordinal);
    }

    [NeedsWimlibFact]
    public async Task ATruncatedPart_IsInvalidEvenWithoutTheHash()
    {
        var (written, root) = await SplitMedium();
        var path = Path.Combine(root, "sources", "install3.swm");
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..100_000]);

        var ex = Assert.Throws<BootrixException>(() => MediaVerifier.CheckSplitSet(root, "sources/install.swm", written));

        Assert.Equal(ErrorCode.SplitSetInvalid, ex.Code);
    }

    [NeedsWimlibFact]
    public async Task APartThatIsNotAWim_IsInvalid()
    {
        var (written, root) = await SplitMedium();
        File.WriteAllBytes(Path.Combine(root, "sources", "install2.swm"), new byte[5000]);

        var ex = Assert.Throws<BootrixException>(() => MediaVerifier.CheckSplitSet(root, "sources/install.swm", written));

        Assert.Equal(ErrorCode.SplitSetInvalid, ex.Code);
    }

    [NeedsWimlibFact]
    public async Task ACorruptSplitPart_FailsTheHashCheckFirst()
    {
        var (written, root) = await SplitMedium();
        var path = Path.Combine(root, "sources", "install2.swm");
        var content = File.ReadAllBytes(path);
        content[^5000] ^= 0xFF;
        File.WriteAllBytes(path, content);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => MediaVerifier.VerifyAsync(root, written));

        Assert.Equal(["sources/install2.swm"], ex.Arguments);
    }
}
