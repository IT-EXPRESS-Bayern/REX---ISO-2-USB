// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class DriverStagerTests : IDisposable
{
    private readonly ScratchFolder _folder = new();
    private readonly CapturingLogger _log = new();

    public void Dispose() => _folder.Dispose();

    private sealed class RecordingUserContext : IUserContext
    {
        public int Calls { get; private set; }

        /// <summary>Runs before the action of call number n (1-based); lets a test change the world between the scan and a read.</summary>
        public Action<int>? Before { get; set; }

        public Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            Calls++;
            Before?.Invoke(Calls);
            return action();
        }
    }

    private sealed class DenyingUserContext : IUserContext
    {
        public Task<T> RunAsync<T>(Func<Task<T>> action) => throw new UnauthorizedAccessException("the user may not read this");
    }

    private string Source(string name, params (string Relative, string Content)[] files)
    {
        var root = Path.Combine(_folder.Root, "src", name);
        Directory.CreateDirectory(root);
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return root;
    }

    private DriverStager Stager(IUserContext? user = null, DriverLimits? limits = null) => new(user ?? new ProcessUserContext(), limits, _log);

    private Task<StagedDriverSet> StageAsync(IReadOnlyList<string> folders, IUserContext? user = null, DriverLimits? limits = null, ProgressLog? progress = null, CancellationToken cancellationToken = default) =>
        Stager(user, limits).StageAsync(folders, _folder.Media, progress ?? new ProgressLog(), cancellationToken);

    [Fact]
    public async Task Stage_CopiesTheTreeBelowWinPEDriverInANumberedFolder()
    {
        var source = Source("Intel RST", ("vmd/iaStorVD.inf", "[Version]"), ("vmd/iaStorVD.sys", "SYS"), ("vmd/iaStorVD.cat", "CAT"), ("readme.txt", "hello"));

        var set = await StageAsync([source]);

        Assert.Equal(_folder.Full("$WinPEDriver$"), set.Directory);
        Assert.Equal("[Version]", _folder.Read("$WinPEDriver$/01-Intel_RST/vmd/iaStorVD.inf"));
        Assert.Equal("SYS", _folder.Read("$WinPEDriver$/01-Intel_RST/vmd/iaStorVD.sys"));
        Assert.Equal("hello", _folder.Read("$WinPEDriver$/01-Intel_RST/readme.txt"));
        Assert.Equal(4, set.FileCount);
        Assert.Equal("[Version]".Length + "SYS".Length + "CAT".Length + "hello".Length, set.Bytes);
        Assert.Equal([_folder.Full("$WinPEDriver$/01-Intel_RST/vmd/iaStorVD.inf")], set.InfFiles);
    }

    [Fact]
    public async Task Stage_NamesTheFoldersByNumberSoEqualNamesDoNotCollide()
    {
        var a = Source("one/Treiber", ("a.inf", "A"));
        var b = Source("two/Treiber", ("b.inf", "B"));

        var set = await StageAsync([a, b]);

        Assert.Equal("A", _folder.Read("$WinPEDriver$/01-Treiber/a.inf"));
        Assert.Equal("B", _folder.Read("$WinPEDriver$/02-Treiber/b.inf"));
        Assert.Equal(2, set.InfFiles.Count);
    }

    [Fact]
    public async Task Stage_FolderNamedTwiceOrInsideAnother_IsCopiedOnce()
    {
        var outer = Source("pack", ("outer.inf", "O"), ("net/inner.inf", "I"));
        var inner = Path.Combine(outer, "net");

        var set = await StageAsync([outer, inner, outer, outer + Path.DirectorySeparatorChar]);

        Assert.Equal(2, set.FileCount);
        Assert.Equal(["01-pack"], Directory.EnumerateDirectories(set.Directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Stage_FolderNamesAreMadeSafeForTheMedium()
    {
        var source = Source("Treiber für Netzwerk & Co: 1.0 ", ("a.inf", "A"));

        await StageAsync([source]);

        var created = Assert.Single(Directory.EnumerateDirectories(_folder.Full("$WinPEDriver$"))).Split(Path.DirectorySeparatorChar)[^1];
        Assert.Matches("^01-[A-Za-z0-9_-]{1,24}$", created);
    }

    [Fact]
    public async Task Stage_NamesLongerThanTheLimit_AreShortened()
    {
        var source = Source(new string('n', 100), ("a.inf", "A"));

        await StageAsync([source]);

        var created = Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(_folder.Full("$WinPEDriver$"))));
        Assert.Equal("01-" + new string('n', 24), created);
    }

    [Fact]
    public async Task Stage_KeepsUnicodeNamesInsideTheTree()
    {
        var source = Source("u", ("驱动程序/网卡.inf", "N"), ("Ünïcödé 😀.sys", "S"));

        await StageAsync([source]);

        Assert.Equal("N", _folder.Read("$WinPEDriver$/01-u/驱动程序/网卡.inf"));
        Assert.Equal("S", _folder.Read("$WinPEDriver$/01-u/Ünïcödé 😀.sys"));
    }

    [Fact]
    public async Task Stage_CopiesLargeFilesByteForByte()
    {
        var source = Source("big", ("a.inf", "A"));
        var data = new byte[3_500_000];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(Path.Combine(source, "big.bin"), data);

        await StageAsync([source]);

        Assert.Equal(SHA256.HashData(data), SHA256.HashData(File.ReadAllBytes(_folder.Full("$WinPEDriver$/01-big/big.bin"))));
    }

    [Fact]
    public async Task Stage_LeavesProgramsBehindAndSaysSo()
    {
        var source = Source("pack", ("a.inf", "A"), ("setup.exe", "MZ"), ("run.ps1", "x"));

        var set = await StageAsync([source]);

        Assert.Equal(1, set.FileCount);
        Assert.False(_folder.Exists("$WinPEDriver$/01-pack/setup.exe"));
        Assert.False(_folder.Exists("$WinPEDriver$/01-pack/run.ps1"));
        Assert.Contains(".exe x1", _log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stage_FilesFromAReadOnlySourceAreWritable()
    {
        var source = Source("ro", ("a.inf", "A"));
        File.SetAttributes(Path.Combine(source, "a.inf"), FileAttributes.ReadOnly);

        await StageAsync([source]);

        Assert.Equal(FileAttributes.Normal, File.GetAttributes(_folder.Full("$WinPEDriver$/01-ro/a.inf")));
        File.SetAttributes(Path.Combine(source, "a.inf"), FileAttributes.Normal);
    }

    [Fact]
    public async Task Stage_KeepsWhatTheImageAlreadyHasInWinPEDriver()
    {
        _folder.Write("$WinPEDriver$/vendor/old.inf", "OLD");
        var source = Source("pack", ("a.inf", "A"));

        await StageAsync([source]);

        Assert.Equal("OLD", _folder.Read("$WinPEDriver$/vendor/old.inf"));
        Assert.Equal("A", _folder.Read("$WinPEDriver$/01-pack/a.inf"));
    }

    [Fact]
    public async Task Stage_ExistingNumberedFolder_IsNotOverwritten()
    {
        _folder.Write("$WinPEDriver$/01-pack/old.inf", "OLD");
        var source = Source("pack", ("a.inf", "A"));

        await StageAsync([source]);

        Assert.Equal("OLD", _folder.Read("$WinPEDriver$/01-pack/old.inf"));
        Assert.Equal("A", _folder.Read("$WinPEDriver$/01-pack-2/a.inf"));
    }

    [Fact]
    public async Task Stage_NoFolders_CreatesNothing()
    {
        var set = await StageAsync([]);

        Assert.Equal(0, set.FileCount);
        Assert.Empty(set.InfFiles);
        Assert.False(_folder.Exists("$WinPEDriver$"));
    }

    [Fact]
    public async Task Stage_RejectedFolder_StopsBeforeAnythingIsWritten()
    {
        var good = Source("good", ("a.inf", "A"));
        var bad = Source("bad", ("only.sys", "S"));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => StageAsync([good, bad]));

        Assert.Equal(ErrorCode.DriverFolderRejected, ex.Code);
        Assert.Equal(bad, ex.Arguments[0]);
        Assert.False(_folder.Exists("$WinPEDriver$"));
    }

    [Fact]
    public async Task Stage_LimitsHoldForAllFoldersTogether()
    {
        var a = Source("a", ("a.inf", new string('x', 600)));
        var b = Source("b", ("b.inf", new string('y', 600)));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => StageAsync([a, b], limits: new DriverLimits { MaxBytes = 1000 }));

        Assert.Equal(b, ex.Arguments[0]);
        Assert.False(_folder.Exists("$WinPEDriver$"));
    }

    [Fact]
    public async Task Stage_ReadsEverythingThroughTheUserContext()
    {
        var source = Source("pack", ("a.inf", "A"), ("b.sys", "B"), ("c.cat", "C"));
        var user = new RecordingUserContext();

        await StageAsync([source], user);

        // One call for the scan of the folder and one for opening each of the three files.
        Assert.Equal(4, user.Calls);
    }

    [Fact]
    public async Task Stage_WhenTheUserContextDeniesAccess_NothingIsReadOrWritten()
    {
        var source = Source("pack", ("a.inf", "A"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => StageAsync([source], new DenyingUserContext()));

        Assert.False(_folder.Exists("$WinPEDriver$"));
    }

    [Fact]
    public async Task Stage_FileThatGrowsAfterTheScan_IsRefusedAndTheCopyRemoved()
    {
        var source = Source("pack", ("a.inf", "A"), ("b.sys", "BBBB"));
        var user = new RecordingUserContext
        {
            Before = call =>
            {
                // Call 1 is the scan; calls 2 and 3 open a.inf and b.sys.
                if (call == 3)
                {
                    File.AppendAllText(Path.Combine(source, "b.sys"), "more");
                }
            },
        };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => StageAsync([source], user));

        Assert.Equal(ErrorCode.DriverFolderRejected, ex.Code);
        Assert.False(_folder.Exists("$WinPEDriver$/01-pack"));
    }

    [SymlinkFact]
    public async Task Stage_FileSwappedForALinkAfterTheScan_IsRefused()
    {
        var source = Source("pack", ("a.inf", "A"), ("b.sys", "B"));
        var secret = Path.Combine(_folder.Root, "secret.sys");
        File.WriteAllText(secret, "SECRET");
        var user = new RecordingUserContext
        {
            Before = call =>
            {
                if (call == 3)
                {
                    var victim = Path.Combine(source, "b.sys");
                    File.Delete(victim);
                    File.CreateSymbolicLink(victim, secret);
                }
            },
        };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => StageAsync([source], user));

        Assert.Equal(ErrorCode.DriverFolderRejected, ex.Code);
        Assert.False(_folder.Exists("$WinPEDriver$"));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_folder.Media, "*", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stage_CancelledInTheMiddle_RemovesWhatWasCopiedAndKeepsWhatWasThere()
    {
        _folder.Write("$WinPEDriver$/vendor/old.inf", "OLD");
        var source = Source("pack", ("a.inf", "A"), ("b.sys", "B"), ("c.cat", "C"));
        using var cts = new CancellationTokenSource();
        var user = new RecordingUserContext { Before = call => { if (call == 3) { cts.Cancel(); } } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StageAsync([source], user, cancellationToken: cts.Token));

        Assert.False(_folder.Exists("$WinPEDriver$/01-pack"));
        Assert.Equal("OLD", _folder.Read("$WinPEDriver$/vendor/old.inf"));
    }

    [Fact]
    public async Task Stage_CancelledOnAnEmptyMedium_LeavesNoWinPEDriverFolder()
    {
        var source = Source("pack", ("a.inf", "A"), ("b.sys", "B"));
        using var cts = new CancellationTokenSource();
        var user = new RecordingUserContext { Before = call => { if (call == 2) { cts.Cancel(); } } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StageAsync([source], user, cancellationToken: cts.Token));

        Assert.False(_folder.Exists("$WinPEDriver$"));
    }

    [Fact]
    public async Task Stage_ReportsProgressFromZeroToOne()
    {
        var source = Source("pack", ("a.inf", new string('a', 2_500_000)), ("b.sys", new string('b', 1_500_000)));
        var progress = new ProgressLog();

        await StageAsync([source], progress: progress);

        progress.AssertMonotonicToOne();
        Assert.True(progress.Values.Count > 3);
    }

    [Fact]
    public async Task Stage_LogsCountsButNoFileContent()
    {
        var source = Source("pack", ("a.inf", "TOP-SECRET-CONTENT"));

        await StageAsync([source]);

        Assert.Contains("Copied 1 driver files", _log.All, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET-CONTENT", _log.All, StringComparison.Ordinal);
    }
}
