// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Library;

public sealed class ImageLibraryTests : IDisposable
{
    private readonly LibraryFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static LibraryImageInfo Info(string name = "SystemRescue 13.02", string version = "13.02") => new()
    {
        Name = name,
        Version = version,
        CatalogId = "rescue-systemrescue",
        VariantId = "13.02-amd64",
        Architecture = "x64",
        Source = "https://example.org/files/systemrescue.iso",
    };

    [Fact]
    public async Task AddingStoresTheImageUnderItsHashWithMetadataAndLeavesTheSource()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("sysrescue.iso", 100_000);

        var result = await library.AddAsync(path, Info());

        Assert.False(result.AlreadyPresent);
        Assert.Equal(sha, result.Entry.Sha256);
        Assert.Equal(100_000, result.Entry.Size);
        Assert.Equal(LibraryFixture.ImagePath(_fx.Local, sha), result.Entry.Path);
        Assert.Equal(LibraryLocation.Local, result.Entry.Location);
        Assert.Equal(_fx.Clock.GetUtcNow(), result.Entry.DownloadedUtc);
        Assert.Equal(_fx.Clock.GetUtcNow(), result.Entry.LastUsedUtc);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(result.Entry.Path));
        Assert.True(File.Exists(path));
        Assert.Equal(
            [$"{sha}.iso", $"{sha}.json"],
            Directory.GetFiles(_fx.Local).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ListingReadsEverythingBackFromDisk()
    {
        var (path, sha) = _fx.File("sysrescue.iso");
        await _fx.Library().AddAsync(path, Info());

        // A new instance has no memory of the first one: the folder is the index.
        var listing = await _fx.Library().ListAsync(CancellationToken.None);

        var entry = Assert.Single(listing.Entries);
        Assert.Empty(listing.Problems);
        Assert.Equal(sha, entry.Sha256);
        Assert.Equal("SystemRescue 13.02", entry.Info.Name);
        Assert.Equal("13.02", entry.Info.Version);
        Assert.Equal("rescue-systemrescue", entry.Info.CatalogId);
        Assert.Equal("13.02-amd64", entry.Info.VariantId);
        Assert.Equal("x64", entry.Info.Architecture);
        Assert.Equal("https://example.org/files/systemrescue.iso", entry.Info.Source);
    }

    [Fact]
    public async Task AnEmptyOrMissingLibraryFolderIsJustEmpty()
    {
        var listing = await _fx.Library().ListAsync(CancellationToken.None);

        Assert.Empty(listing.Entries);
        Assert.Empty(listing.Problems);
        Assert.False(Directory.Exists(_fx.Local));
    }

    [Fact]
    public async Task TheSameContentUnderAnotherNameIsNotStoredTwice()
    {
        var library = _fx.Library();
        var (first, sha) = _fx.File("a.iso", 5000, seed: 7);
        var (second, _) = _fx.File("renamed.img", 5000, seed: 7);
        var added = await library.AddAsync(first, Info());
        _fx.Clock.Advance(TimeSpan.FromDays(3));

        var again = await library.AddAsync(second, new LibraryImageInfo { Name = "Same image, newer note" });

        Assert.True(again.AlreadyPresent);
        Assert.Equal(added.Entry.Path, again.Entry.Path);
        Assert.Equal(added.Entry.DownloadedUtc, again.Entry.DownloadedUtc);
        Assert.Equal(_fx.Clock.GetUtcNow(), again.Entry.LastUsedUtc);
        Assert.Equal("Same image, newer note", again.Entry.Info.Name);
        Assert.Equal("13.02", again.Entry.Info.Version);
        Assert.Equal(2, Directory.GetFiles(_fx.Local).Length);
        Assert.Single((await library.ListAsync(CancellationToken.None)).Entries, e => e.Sha256 == sha);
    }

    [Fact]
    public async Task MovingTakesTheFileAwayAndAMovedDuplicateIsDropped()
    {
        var library = _fx.Library();
        var (first, sha) = _fx.File("a.iso", 3000, seed: 3);
        var (duplicate, _) = _fx.File("b.iso", 3000, seed: 3);

        await library.AddAsync(first, Info(), LibraryAddMode.Move);
        Assert.False(File.Exists(first));

        var again = await library.AddAsync(duplicate, Info(), LibraryAddMode.Move);
        Assert.True(again.AlreadyPresent);
        Assert.False(File.Exists(duplicate));
        Assert.True(File.Exists(LibraryFixture.ImagePath(_fx.Local, sha)));
    }

    [Fact]
    public async Task AddingTheStoredFileItselfNeverDeletesIt()
    {
        var library = _fx.Library();
        var (path, _) = _fx.File("a.iso", 3000, seed: 3);
        var stored = await library.AddAsync(path, Info());

        var again = await library.AddAsync(stored.Entry.Path, Info(), LibraryAddMode.Move);

        Assert.True(again.AlreadyPresent);
        Assert.True(File.Exists(stored.Entry.Path));
        Assert.Single((await library.ListAsync(CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task AKnownDigestSavesHashingAndMoveIsARename()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("download.iso", 20_000, seed: 9);

        var result = await library.AddAsync(path, Info(), LibraryAddMode.Move, knownSha256: sha.ToUpperInvariant());

        Assert.Equal(sha, result.Entry.Sha256);
        Assert.False(File.Exists(path));
        Assert.Equal(LibraryVerifyOutcome.Ok, await library.VerifyAsync(result.Entry, null, CancellationToken.None));
    }

    [Fact]
    public async Task AWrongKnownDigestIsCaughtByVerification()
    {
        var library = _fx.Library();
        var (path, _) = _fx.File("download.iso", 20_000, seed: 9);
        var wrong = Convert.ToHexStringLower(SHA256.HashData("something else"u8));

        var result = await library.AddAsync(path, Info(), knownSha256: wrong);

        Assert.Equal(LibraryVerifyOutcome.ContentChanged, await library.VerifyAsync(result.Entry, null, CancellationToken.None));
    }

    [Fact]
    public async Task AKnownDigestThatContradictsAStoredImageIsRefused()
    {
        var library = _fx.Library();
        var (first, sha) = _fx.File("a.iso", 3000, seed: 3);
        var (other, _) = _fx.File("b.iso", 3001, seed: 4);
        await library.AddAsync(first, Info());

        await Assert.ThrowsAsync<InvalidDataException>(() => library.AddAsync(other, Info(), knownSha256: sha));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("../../etc/passwd")]
    [InlineData("zzzz9877b483ab46d7c3fbe165a0db275e1ae3cfe56a5657e5a47c2f99a99d1e")]
    public async Task ADigestThatIsNotASha256IsRefusedEverywhere(string bad)
    {
        var library = _fx.Library();
        var (path, _) = _fx.File("a.iso");

        await Assert.ThrowsAsync<ArgumentException>(() => library.AddAsync(path, Info(), knownSha256: bad));
        await Assert.ThrowsAsync<ArgumentException>(() => library.FindAsync(bad, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => library.TouchAsync(bad, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => library.RemoveAsync(bad, CancellationToken.None));
    }

    [Fact]
    public async Task AddingAFileThatDoesNotExistFails()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _fx.Library().AddAsync(Path.Combine(_fx.Inbox, "nope.iso"), Info()));
    }

    [Theory]
    [InlineData("image.ISO", "iso")]
    [InlineData("backup.tar.gz", "gz")]
    [InlineData("noextension", "bin")]
    [InlineData("evil.i$o", "bin")]
    [InlineData("long.extension123", "bin")]
    [InlineData("..\\..\\up.iso", "iso")]
    [InlineData("../../up", "bin")]
    [InlineData("trick.json", "bin")]
    [InlineData("trick.tmp", "bin")]
    public async Task OnlyAPlainExtensionOfTheOriginalNameSurvivesInTheStoredName(string fileName, string expected)
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("source.dat", 2000, seed: fileName.Length);

        var result = await library.AddAsync(path, new LibraryImageInfo { FileName = fileName });

        Assert.Equal(LibraryFixture.ImagePath(_fx.Local, sha, expected), result.Entry.Path);
        Assert.Equal(2, Directory.GetFiles(_fx.Local).Length);
        Assert.Empty(Directory.GetDirectories(_fx.Local));
        Assert.Single((await library.ListAsync(CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task DisplayTextIsCleanedAndTheSourceLosesItsTokens()
    {
        var library = _fx.Library();
        var (path, _) = _fx.File("a.iso");

        var result = await library.AddAsync(path, new LibraryImageInfo
        {
            Name = "Bad\u0007 Name\r\nwith lines " + new string('x', 400),
            Source = "https://user:secret@downloads.example.org/files/a.iso?token=abc123#frag",
        });

        Assert.DoesNotContain(result.Entry.Info.Name!, char.IsControl);
        Assert.StartsWith("Bad Namewith lines", result.Entry.Info.Name, StringComparison.Ordinal);
        Assert.Equal(256, result.Entry.Info.Name!.Length);
        Assert.Equal("https://downloads.example.org/files/a.iso", result.Entry.Info.Source);
    }

    [Fact]
    public async Task FindLooksInTheLocalFolderFirstAndFallsBackToTheShare()
    {
        var (path, sha) = _fx.File("a.iso", seed: 5);
        await _fx.Colleague().AddAsync(path, Info("From the NAS"));
        var library = _fx.Library(withShared: true);

        var shared = await library.FindAsync(sha, CancellationToken.None);
        Assert.Equal(LibraryLocation.Shared, shared!.Location);
        Assert.Equal("From the NAS", shared.Info.Name);

        await library.AddAsync(path, Info("Own copy"));
        var local = await library.FindAsync(sha, CancellationToken.None);
        Assert.Equal(LibraryLocation.Local, local!.Location);
        Assert.Equal("Own copy", local.Info.Name);

        Assert.Null(await library.FindAsync(new string('a', 64), CancellationToken.None));
    }

    [Fact]
    public async Task TouchingMarksTheImageAsUsedAndKeepsTheRest()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("a.iso");
        var added = await library.AddAsync(path, Info());
        _fx.Clock.Advance(TimeSpan.FromDays(10));

        Assert.True(await library.TouchAsync(sha, CancellationToken.None));

        var entry = (await library.FindAsync(sha, CancellationToken.None))!;
        Assert.Equal(added.Entry.DownloadedUtc, entry.DownloadedUtc);
        Assert.Equal(_fx.Clock.GetUtcNow(), entry.LastUsedUtc);
        Assert.Equal("SystemRescue 13.02", entry.Info.Name);
        Assert.False(await library.TouchAsync(new string('b', 64), CancellationToken.None));
    }

    [Fact]
    public async Task RemovingDeletesImageAndMetadata()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("a.iso");
        await library.AddAsync(path, Info());

        Assert.True(await library.RemoveAsync(sha, CancellationToken.None));
        Assert.False(await library.RemoveAsync(sha, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(_fx.Local));
        Assert.Empty((await library.ListAsync(CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task SearchMatchesEveryWordInAnyFieldAndAHashPrefix()
    {
        var library = _fx.Library();
        var (a, shaA) = _fx.File("a.iso", seed: 1);
        var (b, _) = _fx.File("b.iso", seed: 2);
        var (c, _) = _fx.File("c.img", seed: 3);
        await library.AddAsync(a, Info("SystemRescue 13.02", "13.02"));
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await library.AddAsync(b, new LibraryImageInfo { Name = "GParted Live", Version = "1.8.1-6", CatalogId = "rescue-gparted-live", Architecture = "x64" });
        _fx.Clock.Advance(TimeSpan.FromMinutes(1));
        await library.AddAsync(c, new LibraryImageInfo { Name = "Memtest86+", Version = "8.10", CatalogId = "rescue-memtest86plus", Language = "de" });

        async Task<string[]> Names(string query) => [.. (await library.SearchAsync(query, CancellationToken.None)).Select(e => e.Info.Name!)];

        Assert.Equal(["Memtest86+", "GParted Live", "SystemRescue 13.02"], await Names(""));
        Assert.Equal(["GParted Live"], await Names("gparted"));
        Assert.Equal(["GParted Live"], await Names("LIVE x64"));
        Assert.Equal(["SystemRescue 13.02"], await Names("rescue-systemrescue"));
        Assert.Equal(["Memtest86+"], await Names("de 8.10"));
        Assert.Equal(["GParted Live"], await Names("rescue 1.8.1"));
        Assert.Equal(["SystemRescue 13.02"], await Names(shaA[..8]));
        Assert.Empty(await Names("gparted memtest"));
        Assert.Empty(await Names("nothing like it"));
        Assert.Empty(await Names("abc"));
    }

    [Fact]
    public async Task VerificationFindsMissingCutOffAndChangedImages()
    {
        var library = _fx.Library();
        var (path, _) = _fx.File("a.iso", 50_000, seed: 2);
        var entry = (await library.AddAsync(path, Info())).Entry;
        var progress = new Progress();

        Assert.Equal(LibraryVerifyOutcome.Ok, await library.VerifyAsync(entry, progress, CancellationToken.None));
        Assert.Equal(50_000, progress.Last);

        var bytes = File.ReadAllBytes(entry.Path);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(entry.Path, bytes);
        Assert.Equal(LibraryVerifyOutcome.ContentChanged, await library.VerifyAsync(entry, null, CancellationToken.None));

        File.WriteAllBytes(entry.Path, bytes[..100]);
        Assert.Equal(LibraryVerifyOutcome.SizeChanged, await library.VerifyAsync(entry, null, CancellationToken.None));

        File.Delete(entry.Path);
        Assert.Equal(LibraryVerifyOutcome.Missing, await library.VerifyAsync(entry, null, CancellationToken.None));
    }

    [Fact]
    public async Task VerificationRefusesFilesThatAreNotInTheLibrary()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("a.iso");
        var elsewhere = new LibraryEntry
        {
            Sha256 = sha,
            Size = 4096,
            Path = path,
            Location = LibraryLocation.Local,
            Info = new LibraryImageInfo(),
            DownloadedUtc = _fx.Clock.GetUtcNow(),
            LastUsedUtc = _fx.Clock.GetUtcNow(),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => library.VerifyAsync(elsewhere, null, CancellationToken.None));
    }

    [Fact]
    public async Task AnAddThatIsCancelledUpFrontCreatesNothing()
    {
        var library = _fx.Library();
        var (path, _) = _fx.File("big.iso", 6 * 1024 * 1024);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.AddAsync(path, Info(), cancellationToken: cts.Token));

        Assert.Empty(Directory.Exists(_fx.Local) ? Directory.GetFiles(_fx.Local) : []);
    }

    [Fact]
    public async Task ParallelAddsOfTheSameContentStoreItOnce()
    {
        var library = _fx.Library();
        var sources = Enumerable.Range(0, 8).Select(i => _fx.File($"copy{i}.iso", 300_000, seed: 11).Path).ToList();

        var results = await Task.WhenAll(sources.Select(s => library.AddAsync(s, Info())));

        Assert.Single(results, r => !r.AlreadyPresent);
        Assert.Equal(7, results.Count(r => r.AlreadyPresent));
        Assert.Equal(2, Directory.GetFiles(_fx.Local).Length);
        Assert.Single((await library.ListAsync(CancellationToken.None)).Entries);
    }

    [Fact]
    public void TheSharedFolderCannotBeTheLocalOne()
    {
        var options = new ImageLibraryOptions { LocalDirectory = _fx.Local, SharedDirectory = _fx.Local + Path.DirectorySeparatorChar };

        Assert.Throws<ArgumentException>(() => new ImageLibrary(options, NullLogger<ImageLibrary>.Instance));
    }

    private sealed class Progress : IProgress<long>
    {
        public long Last { get; private set; }

        public void Report(long value) => Last = value;
    }
}
