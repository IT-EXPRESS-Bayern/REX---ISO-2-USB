// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Tests.Writing.Linux.Support;
using Bootrix.Core.Writing.Linux;

namespace Bootrix.Core.Tests.Writing.Linux;

public sealed class LinuxMediaVerifierTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bootrix-verify-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _root;
    private readonly FileStream _isoStream;
    private readonly IsoContent _content;
    private readonly LinuxBuildResult _result;

    public LinuxMediaVerifierTests()
    {
        Directory.CreateDirectory(_work);
        _root = Path.Combine(_work, "out");
        Directory.CreateDirectory(_root);
        if (!ExternalTools.IsAvailable("xorriso"))
        {
            _isoStream = null!;
            _content = null!;
            _result = null!;
            return;
        }

        var iso = MiniLinuxIso.Build(Path.Combine(_work, "mini.iso"), _work, new MiniIsoOptions());
        var inspection = new ImageInspector().InspectAsync(iso).GetAwaiter().GetResult();
        var target = new TargetOptions { Mode = WriteMode.Extract, PersistenceMegabytes = 64 };
        var plan = LayoutPlanner.Plan(inspection.Profile, target, new DeviceCaps { SizeBytes = 8L * 1024 * 1024 * 1024 });
        _isoStream = File.OpenRead(iso);
        _content = IsoContent.Open(_isoStream);
        var settings = LinuxBuildPlanner.Create(plan, inspection.Profile, LinuxTreeFacts.Scan(_content));
        _result = new LinuxMediaBuilder().Build(_content, new DirectoryVolume(_root), settings);
    }

    public void Dispose()
    {
        _content?.Dispose();
        _isoStream?.Dispose();
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Nothing to be done about leftovers.
        }
    }

    private void Verify() => LinuxMediaVerifier.Verify(_content, new DirectoryVolume(_root), _result);

    [RequiresToolFact("xorriso")]
    public void Verify_WrittenMedium_Passes()
    {
        Verify();
    }

    [RequiresToolFact("xorriso")]
    public void Verify_ReportsTheFileAndOffsetOfAChangedByte()
    {
        var path = Path.Combine(_root, "live", "initrd.img");
        var bytes = File.ReadAllBytes(path);
        bytes[1000] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        var error = Assert.Throws<BootrixException>(Verify);

        Assert.Equal(ErrorCode.VerifyMismatch, error.Code);
        Assert.Equal("live/initrd.img:1000", error.Arguments[0]);
    }

    [RequiresToolFact("xorriso")]
    public void Verify_PatchedFile_IsComparedWithThePatchedBytes()
    {
        var path = Path.Combine(_root, "isolinux", "isolinux.cfg");
        File.AppendAllText(path, "# changed afterwards\n");

        var error = Assert.Throws<BootrixException>(Verify);

        Assert.Equal(ErrorCode.VerifyMismatch, error.Code);
    }

    [RequiresToolFact("xorriso")]
    public void Verify_MissingFile_IsReportedAsMissing()
    {
        File.Delete(Path.Combine(_root, "live", "vmlinuz"));

        var error = Assert.Throws<BootrixException>(Verify);

        Assert.Contains("missing", (string)error.Arguments[0]!, StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso")]
    public void Verify_TruncatedFile_IsReported()
    {
        var path = Path.Combine(_root, "live", "initrd.img");
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..100]);

        Assert.Throws<BootrixException>(Verify);
    }

    [RequiresToolFact("xorriso")]
    public void Verify_LdlinuxSys_IsNotComparedBecauseTheInstallerRewritesIt()
    {
        // The builder marks the file read-only, as Syslinux does; unprivileged users cannot write over that.
        var path = Path.Combine(_root, "ldlinux.sys");
        File.SetAttributes(path, FileAttributes.Normal);
        File.WriteAllBytes(path, new byte[69632]);

        Verify();
    }

    [RequiresToolFact("xorriso")]
    public void Verify_Cancelled_Stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => LinuxMediaVerifier.Verify(_content, new DirectoryVolume(_root), _result, null, cancelled.Token));
    }
}

public sealed class DirectoryVolumeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootrix-dirvol-" + Guid.NewGuid().ToString("N")[..10]);

    public DirectoryVolumeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void CreateFile_CreatesParentsAndWritesTheContent()
    {
        var volume = new DirectoryVolume(_root);

        using (var stream = volume.CreateFile("a/b/c.txt", 3))
        {
            stream.Write("abc"u8);
        }

        Assert.Equal("abc", File.ReadAllText(Path.Combine(_root, "a", "b", "c.txt")));
        Assert.True(volume.FileExists("a/b/c.txt"));
        Assert.Equal(["b"], volume.List("a"));
        Assert.Equal(["a"], volume.List(""));
    }

    [Fact]
    public void CreateFile_ReplacesAnExistingFile_EvenWhenItIsReadOnly()
    {
        var volume = new DirectoryVolume(_root);
        using (var first = volume.CreateFile("f.bin", 4))
        {
            first.Write([1, 2, 3, 4]);
        }

        volume.SetAttributes("f.bin", FileAttributes.ReadOnly);
        using (var second = volume.CreateFile("f.bin", 2))
        {
            second.Write([9, 9]);
        }

        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(_root, "f.bin")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("a/./b")]
    [InlineData("name.")]
    [InlineData("name ")]
    [InlineData("dir./file")]
    public void Paths_ThatEscapeOrWindowsCannotKeep_AreRefused(string path)
    {
        var volume = new DirectoryVolume(_root);

        Assert.Throws<ArgumentException>(() => volume.CreateFile(path, 1));
    }

    [Fact]
    public void Delete_RemovesAReadOnlyFile_AndIgnoresAMissingOne()
    {
        var volume = new DirectoryVolume(_root);
        using (var stream = volume.CreateFile("x.sys", 1))
        {
            stream.WriteByte(1);
        }

        volume.SetAttributes("x.sys", FileAttributes.ReadOnly);
        volume.Delete("x.sys");
        volume.Delete("never-existed");

        Assert.False(volume.FileExists("x.sys"));
    }

    [Fact]
    public void BackslashesAreTreatedAsSeparators()
    {
        var volume = new DirectoryVolume(_root);
        using (var stream = volume.CreateFile("dir\\file.txt", 1))
        {
            stream.WriteByte(1);
        }

        Assert.True(File.Exists(Path.Combine(_root, "dir", "file.txt")));
    }
}
