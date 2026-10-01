// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Library;

namespace Bootrix.Core.Tests.Library;

/// <summary>What the library does with folders it cannot trust: damaged metadata, vanished images, hostile names and a share it must not write to.</summary>
public sealed class LibraryIntegrityTests : IDisposable
{
    private static readonly string Sha = new('a', 64);

    private readonly LibraryFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>Writes a metadata file the way the library would, but with whatever values the test wants to try.</summary>
    private static string Metadata(string sha, string? file, long size = 4096, int schema = 1, bool timestamps = true) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema"] = schema,
            ["sha256"] = sha,
            ["file"] = file,
            ["size"] = size,
            ["downloadedUtc"] = timestamps ? "2026-10-01T12:00:00Z" : null,
            ["lastUsedUtc"] = timestamps ? "2026-10-01T12:00:00Z" : null,
        });

    private string PlaceMetadata(string directory, string sha, string json)
    {
        Directory.CreateDirectory(directory);
        var path = LibraryFixture.MetadataPath(directory, sha);
        File.WriteAllText(path, json);
        return path;
    }

    private string PlaceImage(string directory, string sha, int length, string extension = "iso")
    {
        Directory.CreateDirectory(directory);
        var path = LibraryFixture.ImagePath(directory, sha, extension);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private async Task<LibraryProblem> SingleProblem(ImageLibrary? library = null)
    {
        var listing = await (library ?? _fx.Library()).ListAsync(CancellationToken.None);

        Assert.Empty(listing.Entries);
        return Assert.Single(listing.Problems);
    }

    [Theory]
    [InlineData("../outside.iso")]
    [InlineData("..\\outside.iso")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\x.iso")]
    [InlineData("sub/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.iso")]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.iso")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.ISO")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.iso\0")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.iso\n")]
    [InlineData("")]
    public async Task MetadataThatNamesAnythingButItsOwnImageNameIsRejectedWithoutOpeningIt(string file)
    {
        // A decoy outside the folder: if the library followed the name, it would be listed or touched.
        var decoy = Path.Combine(_fx.Inbox, "outside.iso");
        File.WriteAllBytes(decoy, new byte[4096]);
        PlaceMetadata(_fx.Local, Sha, Metadata(Sha, file));
        var library = _fx.Library();

        var problem = await SingleProblem(library);

        Assert.Equal(LibraryProblemKind.InvalidMetadata, problem.Kind);
        Assert.Null(await library.FindAsync(Sha, CancellationToken.None));
        Assert.Equal(4096, new FileInfo(decoy).Length);

        await library.RepairAsync(CancellationToken.None);
        Assert.True(File.Exists(decoy));
    }

    [Fact]
    public async Task AHashInsideTheFileThatDiffersFromItsNameIsRejected()
    {
        PlaceImage(_fx.Local, Sha, 4096);
        PlaceMetadata(_fx.Local, Sha, Metadata(new string('b', 64), $"{Sha}.iso"));

        var listing = await _fx.Library().ListAsync(CancellationToken.None);

        Assert.Empty(listing.Entries);
        Assert.Contains(listing.Problems, p => p.Kind == LibraryProblemKind.InvalidMetadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"schema\": 1, \"sha256\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("not json at all")]
    [InlineData("{ \"schema\": \"one\" }")]
    public async Task UnreadableMetadataIsAProblemAndNotACrash(string content)
    {
        PlaceImage(_fx.Local, Sha, 4096);
        PlaceMetadata(_fx.Local, Sha, content);

        var listing = await _fx.Library().ListAsync(CancellationToken.None);

        Assert.Empty(listing.Entries);
        Assert.Contains(listing.Problems, p => p.Kind == LibraryProblemKind.UnreadableMetadata);
    }

    [Fact]
    public async Task OversizedMetadataIsNotReadAtAll()
    {
        PlaceMetadata(_fx.Local, Sha, new string(' ', 100 * 1024) + Metadata(Sha, $"{Sha}.iso"));

        Assert.Equal(LibraryProblemKind.UnreadableMetadata, (await SingleProblem()).Kind);
    }

    [Theory]
    [InlineData(2, 4096L, true)]
    [InlineData(0, 4096L, true)]
    [InlineData(1, -1L, true)]
    [InlineData(1, 4096L, false)]
    public async Task MetadataWithAnUnknownSchemaOrNonsenseValuesIsRejected(int schema, long size, bool timestamps)
    {
        PlaceImage(_fx.Local, Sha, 4096);
        PlaceMetadata(_fx.Local, Sha, Metadata(Sha, $"{Sha}.iso", size, schema, timestamps));

        var listing = await _fx.Library().ListAsync(CancellationToken.None);

        Assert.Empty(listing.Entries);
        Assert.Contains(listing.Problems, p => p.Kind == LibraryProblemKind.InvalidMetadata);
    }

    [Fact]
    public async Task AnImageThatWasDeletedBehindTheLibrarysBackIsReportedAndNotListed()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("a.iso");
        var added = await library.AddAsync(path, new LibraryImageInfo { Name = "A" });
        File.Delete(added.Entry.Path);

        var problem = await SingleProblem(library);

        Assert.Equal(LibraryProblemKind.MissingImage, problem.Kind);
        Assert.Null(await library.FindAsync(sha, CancellationToken.None));
    }

    [Fact]
    public async Task AnImageThatWasCutOffIsReportedAsASizeMismatch()
    {
        var library = _fx.Library();
        var (path, _) = _fx.File("a.iso", 10_000);
        var added = await library.AddAsync(path, new LibraryImageInfo());
        File.WriteAllBytes(added.Entry.Path, new byte[5000]);

        var problem = await SingleProblem(library);

        Assert.Equal(LibraryProblemKind.SizeMismatch, problem.Kind);
        Assert.Contains("5000", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADirectoryWithTheImageNameCountsAsMissing()
    {
        PlaceMetadata(_fx.Local, Sha, Metadata(Sha, $"{Sha}.iso"));
        Directory.CreateDirectory(LibraryFixture.ImagePath(_fx.Local, Sha));

        Assert.Equal(LibraryProblemKind.MissingImage, (await SingleProblem()).Kind);
    }

    [Fact]
    public async Task ALinkInPlaceOfTheImageIsNotFollowed()
    {
        var (target, _) = _fx.File("elsewhere.iso", 4096);
        PlaceMetadata(_fx.Local, Sha, Metadata(Sha, $"{Sha}.iso"));
        try
        {
            File.CreateSymbolicLink(LibraryFixture.ImagePath(_fx.Local, Sha), target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating symbolic links needs a privilege on Windows; nothing to check there.
            return;
        }

        Assert.Equal(LibraryProblemKind.LinkNotFollowed, (await SingleProblem()).Kind);
    }

    [Fact]
    public async Task AnImageWithoutMetadataIsReportedAsUnindexedAndKept()
    {
        var image = PlaceImage(_fx.Local, Sha, 100);
        var library = _fx.Library();

        var problem = await SingleProblem(library);
        await library.RepairAsync(CancellationToken.None);

        Assert.Equal(LibraryProblemKind.Unindexed, problem.Kind);
        Assert.True(File.Exists(image));
    }

    [Fact]
    public async Task UnrelatedFilesInTheFolderAreIgnoredSilently()
    {
        Directory.CreateDirectory(_fx.Local);
        File.WriteAllText(Path.Combine(_fx.Local, "README.txt"), "notes");
        File.WriteAllText(Path.Combine(_fx.Local, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(_fx.Local, $"{Sha}.iso.0123456789abcdef.tmp"), "half a metadata file");
        File.WriteAllText(Path.Combine(_fx.Local, "UPPER.json"), "{}");

        var listing = await _fx.Library().ListAsync(CancellationToken.None);

        Assert.Empty(listing.Entries);
        Assert.Empty(listing.Problems);
    }

    [Fact]
    public async Task AddingRestoresAnImageWhoseFileWasDeleted()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("a.iso", 8000, seed: 5);
        var first = await library.AddAsync(path, new LibraryImageInfo { Name = "A" });
        File.Delete(first.Entry.Path);

        var again = await library.AddAsync(path, new LibraryImageInfo { Name = "A" });

        Assert.False(again.AlreadyPresent);
        Assert.Equal(LibraryVerifyOutcome.Ok, await library.VerifyAsync(again.Entry, null, CancellationToken.None));
        Assert.Equal(sha, Assert.Single((await library.ListAsync(CancellationToken.None)).Entries).Sha256);
    }

    [Fact]
    public async Task AnUnindexedFileWithTheSameNameIsReplacedByTheVerifiedContent()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("a.iso", 8000, seed: 5);
        Directory.CreateDirectory(_fx.Local);
        File.WriteAllBytes(LibraryFixture.ImagePath(_fx.Local, sha), new byte[8000]);

        var added = await library.AddAsync(path, new LibraryImageInfo());

        Assert.False(added.AlreadyPresent);
        Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(added.Entry.Path));
    }

    [Fact]
    public async Task RepairRemovesWhatIsBrokenAndNothingElse()
    {
        var library = _fx.Library();
        var (a, shaA) = _fx.File("a.iso", 5000, seed: 1);
        var (b, shaB) = _fx.File("b.iso", 5000, seed: 2);
        var (c, shaC) = _fx.File("c.iso", 5000, seed: 3);
        var (d, shaD) = _fx.File("d.iso", 5000, seed: 4);
        var added = new[] { await library.AddAsync(a, new()), await library.AddAsync(b, new()), await library.AddAsync(c, new()), await library.AddAsync(d, new()) };

        File.Delete(added[0].Entry.Path);                                   // image gone
        File.WriteAllBytes(added[1].Entry.Path, new byte[10]);              // cut off
        File.WriteAllText(LibraryFixture.MetadataPath(_fx.Local, shaC), "garbage");   // metadata damaged, image intact
        var bystander = Path.Combine(_fx.Local, "notes.txt");
        File.WriteAllText(bystander, "keep me");
        var stale = Path.Combine(_fx.Local, $"{shaD}.json.{new string('0', 32)}.tmp");
        var fresh = Path.Combine(_fx.Local, $"{shaD}.json.{new string('1', 32)}.tmp");
        File.WriteAllText(stale, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(stale, _fx.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromHours(3));
        File.SetLastWriteTimeUtc(fresh, _fx.Clock.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(5));

        var result = await library.RepairAsync(CancellationToken.None);

        Assert.Equal(
            new[] { LibraryFixture.MetadataPath(_fx.Local, shaA), added[1].Entry.Path, LibraryFixture.MetadataPath(_fx.Local, shaB), LibraryFixture.MetadataPath(_fx.Local, shaC), stale }.Order(StringComparer.Ordinal),
            result.Removed.Order(StringComparer.Ordinal));
        Assert.True(File.Exists(added[2].Entry.Path), "an intact image whose metadata is damaged stays, as an unindexed file");
        Assert.True(File.Exists(added[3].Entry.Path));
        Assert.True(File.Exists(bystander));
        Assert.True(File.Exists(fresh));

        var listing = await library.ListAsync(CancellationToken.None);
        Assert.Equal(shaD, Assert.Single(listing.Entries).Sha256);
        Assert.Equal(LibraryProblemKind.Unindexed, Assert.Single(listing.Problems).Kind);
        Assert.Empty((await library.RepairAsync(CancellationToken.None)).Removed);
    }

    [Fact]
    public async Task AFolderThatIsOnlyBeingWrittenToDoesNotConfuseTheListing()
    {
        // An image is renamed into place before its metadata is written; a reader in between sees an unindexed image, not an error.
        var (path, sha) = _fx.File("a.iso");
        var library = _fx.Library();
        Directory.CreateDirectory(_fx.Local);
        File.Copy(path, LibraryFixture.ImagePath(_fx.Local, sha, "iso"), overwrite: false);
        Assert.Equal(LibraryProblemKind.Unindexed, (await SingleProblem(library)).Kind);

        await library.AddAsync(path, new LibraryImageInfo());

        var listing = await library.ListAsync(CancellationToken.None);
        Assert.Single(listing.Entries);
        Assert.Empty(listing.Problems);
    }

    [Fact]
    public async Task TheSharedFolderIsIndexedLikeTheLocalOneAndMarkedAsShared()
    {
        var (a, shaA) = _fx.File("nas.iso", seed: 1);
        var (b, shaB) = _fx.File("mine.iso", seed: 2);
        await _fx.Colleague().AddAsync(a, new LibraryImageInfo { Name = "On the NAS" });
        await _fx.Library().AddAsync(b, new LibraryImageInfo { Name = "On my disk" });

        var listing = await _fx.Library(withShared: true).ListAsync(CancellationToken.None);

        Assert.Empty(listing.Problems);
        (string, LibraryLocation)[] expected = [(shaA, LibraryLocation.Shared), (shaB, LibraryLocation.Local)];
        Assert.Equal(expected.Order(), listing.Entries.Select(e => (e.Sha256, e.Location)).Order());
    }

    [Fact]
    public async Task AShareThatIsOfflineIsReportedAndTheLocalLibraryKeepsWorking()
    {
        var (path, sha) = _fx.File("a.iso");
        var library = _fx.Library(withShared: true);
        await library.AddAsync(path, new LibraryImageInfo());

        var listing = await library.ListAsync(CancellationToken.None);

        Assert.Equal(sha, Assert.Single(listing.Entries).Sha256);
        var problem = Assert.Single(listing.Problems);
        Assert.Equal((LibraryLocation.Shared, LibraryProblemKind.FolderUnavailable), (problem.Location, problem.Kind));
        Assert.Null(await library.FindAsync(new string('c', 64), CancellationToken.None));
    }

    [Fact]
    public async Task NothingEverWritesToTheSharedFolder()
    {
        var colleague = _fx.Colleague();
        var (a, shaA) = _fx.File("a.iso", seed: 1);
        var (b, shaB) = _fx.File("b.iso", seed: 2);
        await colleague.AddAsync(a, new LibraryImageInfo { Name = "A", CatalogId = "p", Version = "1" });
        await colleague.AddAsync(b, new LibraryImageInfo { Name = "B", CatalogId = "p", Version = "2" });
        PlaceMetadata(_fx.Shared, new string('d', 64), "broken");                 // damage the library must not repair on the share
        PlaceImage(_fx.Shared, new string('e', 64), 10);
        File.WriteAllText(Path.Combine(_fx.Shared, $"{shaA}.json.{new string('0', 32)}.tmp"), "old temp file");
        File.SetLastWriteTimeUtc(Path.Combine(_fx.Shared, $"{shaA}.json.{new string('0', 32)}.tmp"), DateTime.UtcNow.AddDays(-30));
        var before = LibraryFixture.Snapshot(_fx.Shared);

        var library = _fx.Library(withShared: true);
        var (own, _) = _fx.File("own.iso", seed: 3);
        await library.AddAsync(own, new LibraryImageInfo { Name = "Own" });
        await library.AddAsync(a, new LibraryImageInfo { Name = "A again" });
        await library.ListAsync(CancellationToken.None);
        await library.FindAsync(shaA, CancellationToken.None);
        await library.SearchAsync("A", CancellationToken.None);
        await library.FindDuplicatesAsync(CancellationToken.None);
        Assert.True(await library.TouchAsync(shaA, CancellationToken.None));        // the local copy
        Assert.False(await library.TouchAsync(shaB, CancellationToken.None));       // only on the share: not tracked
        Assert.False(await library.RemoveAsync(shaB, CancellationToken.None));
        var plan = await library.PlanCleanupAsync(new LibraryCleanupPolicy { MaxIdle = TimeSpan.Zero, RemoveCopiesAlsoOnShare = true, KeepVersionsPerProduct = 1, MaxTotalBytes = 0 }, CancellationToken.None);
        await library.CleanupAsync(plan, CancellationToken.None);
        await library.CleanupAsync(plan with { Items = [.. (await library.ListAsync(CancellationToken.None)).Entries.Select(e => new LibraryCleanupItem(e, LibraryCleanupReason.OverQuota))] }, CancellationToken.None);
        await library.RepairAsync(CancellationToken.None);
        var shared = (await library.FindAsync(shaB, CancellationToken.None))!;
        Assert.Equal(LibraryVerifyOutcome.Ok, await library.VerifyAsync(shared, null, CancellationToken.None));

        Assert.Equal(before, LibraryFixture.Snapshot(_fx.Shared));
    }

    [Fact]
    public async Task ACopyThatExistsLocallyAndOnTheShareIsReportedAsADuplicate()
    {
        var (a, shaA) = _fx.File("a.iso", seed: 1);
        var (b, _) = _fx.File("b.iso", seed: 2);
        await _fx.Colleague().AddAsync(a, new LibraryImageInfo());
        await _fx.Colleague().AddAsync(b, new LibraryImageInfo());
        var library = _fx.Library(withShared: true);
        await library.AddAsync(a, new LibraryImageInfo());

        var duplicate = Assert.Single(await library.FindDuplicatesAsync(CancellationToken.None));

        Assert.Equal(shaA, duplicate.Sha256);
        Assert.Equal([LibraryLocation.Local, LibraryLocation.Shared], duplicate.Entries.Select(e => e.Location));
    }

    [Fact]
    public async Task MetadataWrittenByTheLibraryIsAlwaysCompleteJson()
    {
        var library = _fx.Library();
        var (path, sha) = _fx.File("a.iso");
        await library.AddAsync(path, new LibraryImageInfo { Name = "A" });

        using var json = JsonDocument.Parse(File.ReadAllBytes(LibraryFixture.MetadataPath(_fx.Local, sha)));

        Assert.Equal(sha, json.RootElement.GetProperty("sha256").GetString());
        Assert.Equal($"{sha}.iso", json.RootElement.GetProperty("file").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(4096, json.RootElement.GetProperty("size").GetInt64());
        Assert.Equal("A", json.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain(Directory.GetFiles(_fx.Local), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
