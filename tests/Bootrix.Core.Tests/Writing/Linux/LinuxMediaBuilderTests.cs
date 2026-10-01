// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Boot;
using Bootrix.Core.Boot.Syslinux;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Tests.Writing.Linux.Support;
using Bootrix.Core.Writing.Linux;

namespace Bootrix.Core.Tests.Writing.Linux;

/// <summary>The media builder on a plain folder: which files arrive, in what form and with which notes.</summary>
public sealed class LinuxMediaBuilderTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bootrix-build-" + Guid.NewGuid().ToString("N")[..10]);

    public LinuxMediaBuilderTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Scratch files left behind are no reason to fail.
        }
    }

    private sealed record Built(string Iso, string Root, LinuxBuildResult Result, LinuxTreeFacts Facts, MediaPlan Plan);

    private Built Build(MiniIsoOptions? options = null, TargetOptions? target = null, Func<LinuxBuildSettings, LinuxBuildSettings>? adjust = null, IProgress<LinuxCopyProgress>? progress = null, CancellationToken token = default)
    {
        var iso = MiniLinuxIso.Build(Path.Combine(_work, Guid.NewGuid().ToString("N")[..6] + ".iso"), _work, options ?? new MiniIsoOptions());
        var inspection = new ImageInspector().InspectAsync(iso, cancellationToken: token).GetAwaiter().GetResult();
        var plan = LayoutPlanner.Plan(inspection.Profile, target ?? new TargetOptions { Mode = WriteMode.Extract }, new DeviceCaps { SizeBytes = 8L * 1024 * 1024 * 1024 });

        using var stream = File.OpenRead(iso);
        using var content = IsoContent.Open(stream, token);
        var facts = LinuxTreeFacts.Scan(content);
        var settings = LinuxBuildPlanner.Create(plan, inspection.Profile, facts);
        settings = adjust?.Invoke(settings) ?? settings;

        var root = Path.Combine(_work, "out-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        var result = new LinuxMediaBuilder().Build(content, new DirectoryVolume(root), settings, progress, token);
        return new Built(iso, root, result, facts, plan);
    }

    private static string Text(string root, string relative) => File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), Encoding.Latin1);

    [RequiresToolFact("xorriso")]
    public void Build_CopiesEveryFileOfTheImageByteForByte()
    {
        var built = Build();

        using var stream = File.OpenRead(built.Iso);
        using var content = IsoContent.Open(stream);
        foreach (var file in content.Files.Where(f => f.Path is not ("md5sum.txt" or "isolinux/isolinux.cfg")))
        {
            using var source = content.OpenFile(file.Path);
            var expected = new byte[file.Length];
            source.ReadExactly(expected);
            var copied = File.ReadAllBytes(Path.Combine(built.Root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(expected.AsSpan().SequenceEqual(copied), file.Path);
        }
    }

    [RequiresToolFact("xorriso")]
    public void Build_PutsLdlinuxSysFirstAndKeepsTheLoaderOfTheImageOut()
    {
        var extra = new Dictionary<string, byte[]> { ["ldlinux.sys"] = [1, 2, 3], ["ldlinux.bss"] = [4, 5, 6] };

        var built = Build(new MiniIsoOptions { ExtraFiles = extra });

        Assert.Equal("ldlinux.sys", built.Result.Files[0].Path);
        Assert.Equal(WrittenFileSource.Generated, built.Result.Files[0].Source);
        Assert.Equal(69632, new FileInfo(Path.Combine(built.Root, "ldlinux.sys")).Length);
        Assert.False(File.Exists(Path.Combine(built.Root, "ldlinux.bss")));
        Assert.Equal(1, built.Result.Files.Count(f => f.Path == "ldlinux.sys"));
    }

    [RequiresToolFact("xorriso")]
    public void Build_WritesALdlinuxModuleAndARedirectConfigurationIntoTheRoot()
    {
        var built = Build();

        Assert.True(File.Exists(Path.Combine(built.Root, "ldlinux.c32")));
        Assert.Equal(
            "DEFAULT loadconfig\n\nLABEL loadconfig\n  CONFIG /isolinux/isolinux.cfg\n  APPEND /isolinux/\n",
            Text(built.Root, "syslinux.cfg"));
    }

    [RequiresToolFact("xorriso")]
    public void Build_ImageModuleIsKeptWhenTheGenerationIsTheSame()
    {
        var built = Build();

        // The image's own ldlinux.c32 (6.03 in this image) goes next to the core, not the one of the shipped release.
        var rootModule = File.ReadAllBytes(Path.Combine(built.Root, "ldlinux.c32"));
        Assert.Equal(SyslinuxBundle.Shipped.Single(b => b.Id == "6.03").LdlinuxModule.ToArray(), rootModule);
    }

    [RequiresToolFact("xorriso")]
    public void Build_ConfigurationInTheRoot_NeedsNoRedirect_AndNativeLocationsAreRespected()
    {
        var native = new Dictionary<string, byte[]> { ["syslinux/syslinux.cfg"] = Encoding.ASCII.GetBytes("DEFAULT x\n") };

        var built = Build(new MiniIsoOptions { Isolinux = false, Grub = true, ExtraFiles = native, GrubBiosModules = true });

        Assert.Equal("syslinux/syslinux.cfg", built.Facts.SyslinuxConfig);
        Assert.False(File.Exists(Path.Combine(built.Root, "syslinux.cfg")));
    }

    [RequiresToolFact("xorriso")]
    public void Build_OlderGenerationOfSyslinuxWithoutGrub_ReplacesModulesAndAddsTheLibraries()
    {
        var oldModule = new byte[] { 9, 9, 9 };
        var options = new MiniIsoOptions
        {
            IsolinuxBanner = "ISOLINUX 4.05 2011-12-09",
            IncludeLdlinux = false,
            Grub = false,
            ExtraFiles = new Dictionary<string, byte[]> { ["isolinux/menu.c32"] = oldModule },
        };

        var built = Build(options);

        var bundle = built.Result.Bios.Syslinux!.Bundle;
        Assert.Equal(SyslinuxMatch.Different, built.Result.Bios.Syslinux.Match);
        Assert.Equal(bundle.LdlinuxModule.ToArray(), File.ReadAllBytes(Path.Combine(built.Root, "ldlinux.c32")));
        Assert.True(bundle.TryGetModule("menu.c32", out var menu));
        Assert.Equal(menu.ToArray(), File.ReadAllBytes(Path.Combine(built.Root, "isolinux", "menu.c32")));
        Assert.True(File.Exists(Path.Combine(built.Root, "isolinux", "libcom32.c32")));
        Assert.True(File.Exists(Path.Combine(built.Root, "isolinux", "libutil.c32")));
        Assert.Contains(built.Result.Notices, n => n.Key == LinuxNoticeKeys.SyslinuxModulesReplaced);
    }

    [RequiresToolFact("xorriso")]
    [SuppressMessage("Security", "CA5351", Justification = "md5sum.txt is an MD5 list; the test recomputes the values the list is checked against.")]
    public void Build_WithPersistence_PatchesTheConfigsAndKeepsTheChecksumListInStep()
    {
        var built = Build(target: new TargetOptions { Mode = WriteMode.Extract, PersistenceMegabytes = 64 });

        var patched = Text(built.Root, "isolinux/isolinux.cfg");
        Assert.Contains("boot=live persistence components", patched, StringComparison.Ordinal);
        Assert.Contains("boot=live persistence", Text(built.Root, "boot/grub/grub.cfg"), StringComparison.Ordinal);
        Assert.Equal(["boot/grub/grub.cfg", "isolinux/isolinux.cfg"], built.Result.Patches.Select(p => p.Path).Order(StringComparer.Ordinal));

        var list = Text(built.Root, "md5sum.txt");
        var expected = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(Path.Combine(built.Root, "isolinux", "isolinux.cfg")))).ToLowerInvariant();
        Assert.Contains($"{expected}  ./isolinux/isolinux.cfg", list, StringComparison.Ordinal);
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var path = line[(line.IndexOf("  ./", StringComparison.Ordinal) + 4)..];
            var actual = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(Path.Combine(built.Root, path.Replace('/', Path.DirectorySeparatorChar))))).ToLowerInvariant();
            if (path != "md5sum.txt")
            {
                Assert.True(line.StartsWith(actual, StringComparison.Ordinal), path);
            }
        }

        Assert.Contains(built.Result.Notices, n => n.Key == LinuxNoticeKeys.PersistenceParameterAdded);
        Assert.Contains(built.Result.Notices, n => n.Key == LinuxNoticeKeys.ChecksumsUpdated);
    }

    [RequiresToolFact("xorriso")]
    public void Build_WithoutPersistenceOrLabelChange_LeavesEveryFileUntouched()
    {
        var built = Build();

        Assert.Empty(built.Result.Patches);
        Assert.All(built.Result.Files.Where(f => f.Source == WrittenFileSource.Patched), f => Assert.Fail(f.Path));
    }

    [RequiresToolFact("xorriso")]
    public void Build_PersistenceForAFamilyWithoutRecipe_SaysSoInsteadOfGuessing()
    {
        var built = Build(
            target: new TargetOptions { Mode = WriteMode.Extract, PersistenceMegabytes = 64 },
            adjust: settings => settings with { Family = "something-new", Traits = Bootrix.Core.Images.Policy.FamilyTraits.None });

        Assert.Contains(built.Result.Notices, n => n.Key == LinuxNoticeKeys.PersistenceNotPatched);
        Assert.Contains("boot=live components", Text(built.Root, "isolinux/isolinux.cfg"), StringComparison.Ordinal);
        Assert.DoesNotContain("persistence", Text(built.Root, "isolinux/isolinux.cfg"), StringComparison.Ordinal);
    }

    [RequiresToolFact("xorriso")]
    public void Build_NamesThatDifferOnlyInCase_AreNotWrittenTwice()
    {
        // The file tree of the image already folds such names, which a FAT or NTFS volume could not hold apart either.
        var extra = new Dictionary<string, byte[]> { ["docs/README"] = [1], ["docs/readme"] = [2] };

        var built = Build(new MiniIsoOptions { ExtraFiles = extra });

        Assert.Single(Directory.EnumerateFiles(Path.Combine(built.Root, "docs")));
    }

    [RequiresToolFact("xorriso")]
    public void Build_GeneratedFileThatCollidesWithAnImageFile_KeepsTheImageFile()
    {
        // The image's root already has a syslinux.cfg of its own that points elsewhere; the redirect must not replace it.
        var own = new Dictionary<string, byte[]> { ["syslinux.cfg"] = Encoding.ASCII.GetBytes("DEFAULT own\n") };

        var built = Build(new MiniIsoOptions { ExtraFiles = own });

        Assert.Equal("DEFAULT own\n", Text(built.Root, "syslinux.cfg"));
        Assert.Equal(1, built.Result.Files.Count(f => f.Path == "syslinux.cfg"));
    }

    [RequiresToolFact("xorriso")]
    public void Build_EfiLoadersOnlyInTheBootImage_AreCopiedToTheRoot()
    {
        var built = Build(new MiniIsoOptions { EfiLoader = false, EfiBootImage = true });

        var loader = File.ReadAllBytes(Path.Combine(built.Root, "EFI", "BOOT", "BOOTX64.EFI"));
        Assert.StartsWith("MZ loader from the El Torito EFI image", Encoding.ASCII.GetString(loader), StringComparison.Ordinal);
        Assert.Contains(built.Result.Notices, n => n.Key == LinuxNoticeKeys.EfiLoadersFromBootImage);
    }

    [RequiresToolFact("xorriso")]
    public void Build_DanglingFallbackLoader_IsReplacedFromTheBootImage()
    {
        var stub = new Dictionary<string, byte[]> { ["EFI/BOOT/BOOTX64.EFI"] = [] };

        var built = Build(new MiniIsoOptions { EfiLoader = false, EfiBootImage = true, ExtraFiles = stub });

        Assert.True(built.Facts.HasBrokenFallbackLoader);
        Assert.True(new FileInfo(Path.Combine(built.Root, "EFI", "BOOT", "BOOTX64.EFI")).Length > 256);
        Assert.Single(built.Result.Files, f => f.Path == "EFI/BOOT/BOOTX64.EFI");
    }

    [RequiresToolFact("xorriso")]
    public void Build_ChecksEfiLoadersOfAUefiMedium_AndToleratesProgramsThatAreNotPe()
    {
        var built = Build();

        Assert.NotNull(built.Result.Efi);
        Assert.Contains(built.Result.Notices, n => n.Key.StartsWith("Efi.", StringComparison.Ordinal));
    }

    [RequiresToolFact("xorriso")]
    public void Build_BiosOnlyMedium_SkipsTheEfiCheck()
    {
        var built = Build(target: new TargetOptions { Mode = WriteMode.Extract, Firmware = TargetFirmware.Bios });

        Assert.Null(built.Result.Efi);
    }

    [RequiresToolFact("xorriso")]
    public void Build_ReportsProgressUpToTheTotalSize()
    {
        var reports = new List<LinuxCopyProgress>();
        var big = new Dictionary<string, byte[]> { ["data/big.bin"] = new byte[9 * 1024 * 1024] };

        var built = Build(new MiniIsoOptions { ExtraFiles = big }, progress: new Progress<LinuxCopyProgress>(reports.Add));
        Thread.Sleep(200);

        Assert.NotEmpty(reports);
        Assert.True(reports.Max(r => r.BytesDone) >= 8 * 1024 * 1024);
        Assert.Equal(built.Result.Bytes, built.Result.Files.Sum(f => f.Length));
    }

    [RequiresToolFact("xorriso")]
    public void Build_Cancelled_StopsWithoutFinishing()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Build(token: cancelled.Token));
    }

    [RequiresToolFact("xorriso")]
    public void Build_VolumeThatFails_ReportsTheFileThatCouldNotBeWritten()
    {
        var iso = MiniLinuxIso.Build(Path.Combine(_work, "failing.iso"), _work, new MiniIsoOptions());
        using var stream = File.OpenRead(iso);
        using var content = IsoContent.Open(stream);
        var facts = LinuxTreeFacts.Scan(content);
        var settings = new LinuxBuildSettings { Bios = new BiosBootDecision(BiosLoader.None, null, 0, 0, []) };

        var error = Assert.Throws<BootrixException>(() => new LinuxMediaBuilder().Build(content, new FailingVolume(), settings));

        Assert.Equal(ErrorCode.FileCopyFailed, error.Code);
        Assert.NotNull(facts);
    }

    private sealed class FailingVolume : ITargetVolume
    {
        public void CreateDirectory(string path)
        {
        }

        public Stream CreateFile(string path, long expectedLength) => throw new IOException("There is not enough space on the disk.");

        public bool FileExists(string path) => false;

        public IReadOnlyList<string> List(string directory) => [];

        public Stream OpenRead(string path) => throw new FileNotFoundException(path);

        public void SetAttributes(string path, FileAttributes attributes)
        {
        }

        public void Delete(string path)
        {
        }
    }
}
