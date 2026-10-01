// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

/// <summary>Symbolic links can be created here; on Windows that needs a privilege the test account may lack.</summary>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        var probe = Path.Combine(Path.GetTempPath(), "bootrix-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.CreateSymbolicLink(probe, "target");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Skip = "symbolic links cannot be created";
        }
        finally
        {
            try
            {
                File.Delete(probe);
            }
            catch (IOException)
            {
                // Nothing to clean up.
            }
        }
    }
}

/// <summary>Names that Windows forbids can only be created on a system that allows them.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "needs a file system that accepts names Windows forbids";
        }
    }
}

/// <summary>File permissions only hold back an ordinary user; the superuser reads everything.</summary>
public sealed class UnixPermissionFactAttribute : FactAttribute
{
    public UnixPermissionFactAttribute()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            Skip = "needs an ordinary user on a system with Unix permissions";
        }
    }
}

public sealed class DriverFolderScannerTests : IDisposable
{
    private readonly ScratchFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Drivers => Path.Combine(_folder.Root, "drivers");

    private string Make(string relative, string content = "data")
    {
        var path = Path.Combine(Drivers, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static BootrixException Rejected(Func<DriverFolderListing> scan)
    {
        var ex = Assert.Throws<BootrixException>(scan);
        Assert.Equal(ErrorCode.DriverFolderRejected, ex.Code);
        return ex;
    }

    private BootrixException RejectedScan(DriverLimits? limits = null) => Rejected(() => DriverFolderScanner.Scan(Drivers, limits));

    private static string Reason(BootrixException ex) => Assert.IsType<string>(ex.Arguments[1]);

    [Fact]
    public void Scan_ListsDriverFilesWithRelativePathsAndSizes()
    {
        Make("net/e1000.inf", "[Version]");
        Make("net/e1000.sys", "1234567890");
        Make("net/e1000.cat");
        Make("root.inf");

        var listing = DriverFolderScanner.Scan(Drivers);

        Assert.Equal(Drivers, listing.Root);
        Assert.Equal(["net/e1000.cat", "net/e1000.inf", "net/e1000.sys", "root.inf"], listing.Files.Select(f => f.RelativePath.Replace('\\', '/')));
        Assert.Equal(10, listing.Files.Single(f => f.RelativePath.EndsWith("e1000.sys", StringComparison.Ordinal)).Length);
        Assert.Equal(4 + 9 + 10 + 4, listing.Bytes);
        Assert.Empty(listing.Skipped);
    }

    [Fact]
    public void Scan_TrailingSeparatorOnTheFolder_IsAccepted()
    {
        Make("a.inf");

        var listing = DriverFolderScanner.Scan(Drivers + Path.DirectorySeparatorChar);

        Assert.Equal(Drivers, listing.Root);
        Assert.Single(listing.Files);
    }

    [Fact]
    public void Scan_ProgramsAndScriptsAreLeftOutAndCounted()
    {
        Make("a.inf");
        Make("setup.exe");
        Make("Install.EXE");
        Make("tool/unpack.msi");
        Make("run.bat");
        Make("run.cmd");
        Make("run.ps1");
        Make("run.vbs");
        Make("link.lnk");
        Make("README");

        var listing = DriverFolderScanner.Scan(Drivers);

        Assert.Equal(["a.inf"], listing.Files.Select(f => f.RelativePath));
        Assert.Equal(2, listing.Skipped[".exe"]);
        Assert.Equal(1, listing.Skipped[".msi"]);
        Assert.Equal(1, listing.Skipped[".bat"]);
        Assert.Equal(1, listing.Skipped[".ps1"]);
        Assert.Equal(1, listing.Skipped["(none)"]);
    }

    [Fact]
    public void Scan_ExtensionsAreMatchedWithoutRegardToCase()
    {
        Make("A.INF");
        Make("b.SyS");
        Make("c.Cat");

        var listing = DriverFolderScanner.Scan(Drivers);

        Assert.Equal(3, listing.Files.Count);
        Assert.True(listing.Files.Single(f => f.RelativePath == "A.INF").IsInf);
    }

    [Theory]
    [InlineData(".inf")]
    [InlineData(".sys")]
    [InlineData(".cat")]
    [InlineData(".dll")]
    [InlineData(".bin")]
    public void AllowedExtensions_HoldTheFilesOfADriverPackage(string extension)
    {
        Assert.Contains(extension, DriverFolderScanner.AllowedExtensions);
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".msi")]
    [InlineData(".bat")]
    [InlineData(".cmd")]
    [InlineData(".ps1")]
    [InlineData(".vbs")]
    [InlineData(".js")]
    [InlineData(".scr")]
    [InlineData(".com")]
    [InlineData(".lnk")]
    [InlineData(".hta")]
    [InlineData(".cpl")]
    public void AllowedExtensions_HoldNoProgramOrScript(string extension)
    {
        Assert.DoesNotContain(extension, DriverFolderScanner.AllowedExtensions);
    }

    [Fact]
    public void Scan_FolderWithoutAnInf_IsRejected()
    {
        Make("a.sys");
        Make("b.cat");

        var ex = RejectedScan();

        Assert.Contains(".inf", Reason(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_OnlyProgramsInTheFolder_IsRejected()
    {
        Make("setup.exe");

        RejectedScan();
    }

    [Fact]
    public void Scan_MissingFolder_IsRejected()
    {
        Rejected(() => DriverFolderScanner.Scan(Path.Combine(_folder.Root, "nothing")));
    }

    [Fact]
    public void Scan_AFileInsteadOfAFolder_IsRejected()
    {
        Make("a.inf");

        Rejected(() => DriverFolderScanner.Scan(Path.Combine(Drivers, "a.inf")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("drivers")]
    [InlineData("drivers/net")]
    [InlineData("./drivers")]
    public void Scan_RelativeOrEmptyPath_IsRejected(string path)
    {
        Rejected(() => DriverFolderScanner.Scan(path));
    }

    [Fact]
    public void Scan_PathWithParentReferences_IsRejected()
    {
        Make("a.inf");
        var sneaky = Path.Combine(Drivers, "..", "drivers");

        Rejected(() => DriverFolderScanner.Scan(sneaky));
        Rejected(() => DriverFolderScanner.Scan(Drivers + "/../drivers"));
        Rejected(() => DriverFolderScanner.Scan(Drivers + "\\..\\drivers"));
    }

    [Fact]
    public void Scan_DriveRoot_IsRejected()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        var ex = Rejected(() => DriverFolderScanner.Scan(root));

        Assert.Equal(root, ex.Arguments[0]);
    }

    [SymlinkFact]
    public void Scan_SymbolicLinkedFolderInside_IsRejected()
    {
        Make("a.inf");
        var outside = Path.Combine(_folder.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.inf"), "x");
        Directory.CreateSymbolicLink(Path.Combine(Drivers, "escape"), outside);

        var ex = RejectedScan();

        Assert.Contains("escape", Reason(ex), StringComparison.Ordinal);
    }

    [SymlinkFact]
    public void Scan_SymbolicLinkedFileInside_IsRejected()
    {
        Make("a.inf");
        var secret = Path.Combine(_folder.Root, "secret.sys");
        File.WriteAllText(secret, "secret");
        File.CreateSymbolicLink(Path.Combine(Drivers, "disguised.sys"), secret);

        var ex = RejectedScan();

        Assert.Contains("disguised.sys", Reason(ex), StringComparison.Ordinal);
    }

    [SymlinkFact]
    public void Scan_BrokenLink_IsRejected()
    {
        Make("a.inf");
        File.CreateSymbolicLink(Path.Combine(Drivers, "dangling.sys"), Path.Combine(_folder.Root, "gone"));

        RejectedScan();
    }

    [SymlinkFact]
    public void Scan_LinkToTheFolderItself_IsRejectedWithoutLooping()
    {
        Make("a.inf");
        Directory.CreateSymbolicLink(Path.Combine(Drivers, "loop"), Drivers);

        RejectedScan();
    }

    [SymlinkFact]
    public void Scan_LinkDeepInTheTree_IsFound()
    {
        Make("x/y/z/a.inf");
        File.CreateSymbolicLink(Path.Combine(Drivers, "x", "y", "z", "b.sys"), Path.Combine(_folder.Root, "gone"));

        var ex = RejectedScan();

        Assert.Contains("b.sys", Reason(ex), StringComparison.Ordinal);
    }

    [SymlinkFact]
    public void Scan_HiddenLink_IsFoundToo()
    {
        Make("a.inf");
        Directory.CreateSymbolicLink(Path.Combine(Drivers, ".hidden"), _folder.Root);

        RejectedScan();
    }

    [SymlinkFact]
    public void Scan_TheFolderItselfBeingALink_IsRejected()
    {
        Make("a.inf");
        var alias = Path.Combine(_folder.Root, "alias");
        Directory.CreateSymbolicLink(alias, Drivers);

        Rejected(() => DriverFolderScanner.Scan(alias));
    }

    [Fact]
    public void Scan_OrdinaryFiles_AreNotTakenForLinks()
    {
        Make("a.inf");

        Assert.Single(DriverFolderScanner.Scan(Drivers).Files);
    }

    [Theory]
    [InlineData("net/Treiber für Netzwerk.inf")]
    [InlineData("驱动程序/网卡.inf")]
    [InlineData("😀/emoji.inf")]
    [InlineData("Ünïcödé.sys")]
    [InlineData("é.inf")]
    [InlineData("العربية.cat")]
    public void Scan_UnicodeNames_AreAccepted(string relative)
    {
        Make("base.inf");
        Make(relative);

        var listing = DriverFolderScanner.Scan(Drivers);

        Assert.Contains(listing.Files, f => f.RelativePath.Replace('\\', '/') == relative);
    }

    [Theory]
    [InlineData("a.inf", true)]
    [InlineData("Ünï.inf", true)]
    [InlineData("驱动.inf", true)]
    [InlineData("😀.inf", true)]
    [InlineData("COM10.inf", true)]
    [InlineData("console.inf", true)]
    [InlineData("AUX2.sys", true)]
    [InlineData("nul_driver.inf", true)]
    [InlineData("a b.inf", true)]
    [InlineData(" leading.inf", true)]
    [InlineData("", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    [InlineData("a:b.inf", false)]
    [InlineData("a*b.inf", false)]
    [InlineData("a?b.inf", false)]
    [InlineData("a\"b.inf", false)]
    [InlineData("a<b.inf", false)]
    [InlineData("a>b.inf", false)]
    [InlineData("a|b.inf", false)]
    [InlineData("a/b.inf", false)]
    [InlineData("a\\b.inf", false)]
    [InlineData("a\tb.inf", false)]
    [InlineData("a\u0001b.inf", false)]
    [InlineData("a\nb.inf", false)]
    [InlineData("CON", false)]
    [InlineData("con.inf", false)]
    [InlineData("NUL.txt", false)]
    [InlineData("Aux.sys", false)]
    [InlineData("PRN.dll", false)]
    [InlineData("COM1", false)]
    [InlineData("com9.inf", false)]
    [InlineData("LPT3.cat", false)]
    [InlineData("file.inf:stream", false)]
    public void IsValidName_FollowsTheWindowsRules(string name, bool expected)
    {
        Assert.Equal(expected, DriverFolderScanner.IsValidName(name));
    }

    [Fact]
    public void IsValidName_LoneSurrogates_AreRejected()
    {
        Assert.False(DriverFolderScanner.IsValidName("a\uD800b.inf"));
        Assert.False(DriverFolderScanner.IsValidName("a\uDC00b.inf"));
        Assert.False(DriverFolderScanner.IsValidName("end\uD83D"));
        Assert.True(DriverFolderScanner.IsValidName("pair\uD83D\uDE00.inf"));
    }

    [Fact]
    public void IsValidName_LengthLimitIs255()
    {
        Assert.True(DriverFolderScanner.IsValidName(new string('a', 255)));
        Assert.False(DriverFolderScanner.IsValidName(new string('a', 256)));
        Assert.False(DriverFolderScanner.IsValidName(new string('a', 4000) + ".inf"));
    }

    [UnixFact]
    public void Scan_NamesWindowsForbids_AreRejected()
    {
        foreach (var name in new[] { "a:b.inf", "a*b.inf", "a?b.inf", "a|b.inf", "trailing.dot.", "CON.inf", "a\\b.inf" })
        {
            var folder = new ScratchFolder();
            try
            {
                var drivers = Path.Combine(folder.Root, "d");
                Directory.CreateDirectory(drivers);
                File.WriteAllText(Path.Combine(drivers, "ok.inf"), "x");
                File.WriteAllText(Path.Combine(drivers, name), "x");

                var ex = Assert.Throws<BootrixException>(() => DriverFolderScanner.Scan(drivers));

                Assert.Equal(ErrorCode.DriverFolderRejected, ex.Code);
            }
            finally
            {
                folder.Dispose();
            }
        }
    }

    [UnixFact]
    public void Scan_FolderWithAForbiddenNameIsRejectedToo()
    {
        Make("ok.inf");
        Make("bad:name/a.inf");

        RejectedScan();
    }

    [Fact]
    public void Scan_VeryLongNamesAndPaths_AreRejected()
    {
        Make("a.inf");
        Make(new string('x', 200) + ".inf");

        var ex = RejectedScan();

        Assert.Contains(new string('x', 50), Reason(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_DeepPathAboveTheLimit_IsRejected()
    {
        Make("a.inf");
        Make(string.Join('/', Enumerable.Repeat(new string('d', 50), 4)) + "/deep.inf");

        RejectedScan();
    }

    [Fact]
    public void Scan_PathJustWithinTheLimit_IsAccepted()
    {
        Make("a.inf");
        var name = new string('n', 190 - ".inf".Length) + ".inf";
        Make(name);

        var listing = DriverFolderScanner.Scan(Drivers);

        Assert.Equal(2, listing.Files.Count);
    }

    [Fact]
    public void Scan_TooManyFiles_IsRejected()
    {
        for (var i = 0; i < 5; i++)
        {
            Make($"f{i}.inf");
        }

        var ex = RejectedScan(new DriverLimits { MaxFiles = 4 });

        Assert.Contains("4", Reason(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_ExactlyAsManyFilesAsAllowed_IsAccepted()
    {
        for (var i = 0; i < 4; i++)
        {
            Make($"f{i}.inf");
        }

        Assert.Equal(4, DriverFolderScanner.Scan(Drivers, new DriverLimits { MaxFiles = 4 }).Files.Count);
    }

    [Fact]
    public void Scan_TooMuchData_IsRejected()
    {
        Make("a.inf", new string('x', 600));
        Make("b.sys", new string('y', 600));

        RejectedScan(new DriverLimits { MaxBytes = 1000 });
    }

    [Fact]
    public void Scan_ProgramsDoNotCountAgainstTheLimits()
    {
        Make("a.inf");
        Make("huge.exe", new string('x', 5000));

        var listing = DriverFolderScanner.Scan(Drivers, new DriverLimits { MaxBytes = 100 });

        Assert.Single(listing.Files);
    }

    [Fact]
    public void Scan_NestedDeeperThanAllowed_IsRejected()
    {
        Make("a.inf");
        Make("1/2/3/deep.inf");

        var ex = RejectedScan(new DriverLimits { MaxDepth = 2 });

        Assert.Contains("2", Reason(ex), StringComparison.Ordinal);
    }

    [Fact]
    public void Scan_NestedExactlyAsDeepAsAllowed_IsAccepted()
    {
        Make("1/2/ok.inf");

        Assert.Single(DriverFolderScanner.Scan(Drivers, new DriverLimits { MaxDepth = 2 }).Files);
    }

    [Fact]
    public void Scan_HiddenFilesAreIncluded()
    {
        Make(".hidden.inf");
        var path = Make("shy.inf");
        File.SetAttributes(path, FileAttributes.Hidden);

        var listing = DriverFolderScanner.Scan(Drivers);

        Assert.Equal(2, listing.Files.Count);
    }

    [Fact]
    public void Scan_AlreadyCancelled_Throws()
    {
        Make("a.inf");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => DriverFolderScanner.Scan(Drivers, cancellationToken: cts.Token));
    }

    [Fact]
    public void Scan_ResultIsInAStableOrder()
    {
        foreach (var name in new[] { "z.inf", "b/a.inf", "a.inf", "B.inf" })
        {
            Make(name);
        }

        var first = DriverFolderScanner.Scan(Drivers).Files.Select(f => f.RelativePath);
        var second = DriverFolderScanner.Scan(Drivers).Files.Select(f => f.RelativePath);

        Assert.Equal(first, second);
    }

    [UnixPermissionFact]
    public void Scan_FolderThatCannotBeRead_IsRejectedNamingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Make("a.inf");
        Make("locked/b.inf");
        var locked = Path.Combine(Drivers, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var ex = RejectedScan();

            Assert.Contains("locked", Reason(ex), StringComparison.Ordinal);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Scan_TheMessageNamesTheFolderAndNotTheContent()
    {
        Make("a.sys");

        var ex = RejectedScan();

        Assert.Equal(Drivers, ex.Arguments[0]);
        Assert.DoesNotContain("data", Reason(ex), StringComparison.Ordinal);
    }
}
