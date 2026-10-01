// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Library;

namespace Bootrix.Core.Tests.Library;

public sealed class LibraryCleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly LibraryFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static string Sha(char c) => new(c, 64);

    private static LibraryEntry Entry(char id, long size = 100, int idleDays = 0, string? product = null, string? version = null, string? architecture = null, string? language = null, int downloadedDaysAgo = 0) => new()
    {
        Sha256 = Sha(id),
        Size = size,
        Path = $"/library/{Sha(id)}.iso",
        Location = LibraryLocation.Local,
        Info = new LibraryImageInfo { CatalogId = product, Version = version, Architecture = architecture, Language = language },
        DownloadedUtc = Now.AddDays(-downloadedDaysAgo),
        LastUsedUtc = Now.AddDays(-idleDays),
    };

    private static LibraryCleanupPlan Plan(IReadOnlyList<LibraryEntry> entries, LibraryCleanupPolicy policy, params char[] onShare) =>
        LibraryCleanupPlanner.Plan(entries, new HashSet<string>(onShare.Select(Sha)), policy, Now);

    private static string[] Removed(LibraryCleanupPlan plan) => [.. plan.Items.Select(i => i.Entry.Sha256[..1])];

    [Fact]
    public void WithoutLimitsNothingIsRemoved()
    {
        var plan = Plan([Entry('a', idleDays: 1000), Entry('b', idleDays: 2000)], new LibraryCleanupPolicy());

        Assert.Empty(plan.Items);
        Assert.Equal(0, plan.BytesToFree);
        Assert.Equal(200, plan.BytesRemaining);
    }

    [Fact]
    public void ImagesThatWereNotUsedForLongerThanTheLimitAreRemoved()
    {
        var plan = Plan(
            [Entry('a', idleDays: 10), Entry('b', idleDays: 30), Entry('c', idleDays: 31), Entry('d', idleDays: 400)],
            new LibraryCleanupPolicy { MaxIdle = TimeSpan.FromDays(30) });

        Assert.Equal(["c", "d"], Removed(plan));
        Assert.All(plan.Items, i => Assert.Equal(LibraryCleanupReason.NotUsedForLong, i.Reason));
    }

    [Fact]
    public void TheSizeLimitRemovesTheLeastRecentlyUsedFirstAndStopsAsSoonAsItIsMet()
    {
        LibraryEntry[] entries = [Entry('a', 100, idleDays: 5), Entry('b', 100, idleDays: 50), Entry('c', 100, idleDays: 20), Entry('d', 100, idleDays: 1)];

        Assert.Equal(["b"], Removed(Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 300 })));
        Assert.Equal(["b", "c"], Removed(Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 200 })));
        Assert.Equal(["a", "b", "c"], Removed(Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 100 })));
        Assert.Equal(["a", "b", "c", "d"], Removed(Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 0 })));
        Assert.Empty(Removed(Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 400 })));

        var plan = Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 200 });
        Assert.All(plan.Items, i => Assert.Equal(LibraryCleanupReason.OverQuota, i.Reason));
        Assert.Equal((200, 200), (plan.BytesToFree, plan.BytesRemaining));
    }

    [Fact]
    public void ImagesThatAreEquallyOldGoByDownloadDateThenByHash()
    {
        LibraryEntry[] entries = [Entry('c', idleDays: 9, downloadedDaysAgo: 1), Entry('a', idleDays: 9, downloadedDaysAgo: 5), Entry('b', idleDays: 9, downloadedDaysAgo: 5)];

        Assert.Equal(["a", "b"], Removed(Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 100 })));
    }

    [Fact]
    public void RecencyDecidesWhoGoesFirstAndTheSizeWhenToStop()
    {
        LibraryEntry[] entries = [Entry('a', 1000, idleDays: 90), Entry('b', 10, idleDays: 1), Entry('c', 10, idleDays: 2)];

        Assert.Equal(["a"], Removed(Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 500 })));
        Assert.Equal(["a", "c"], Removed(Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 10 })));
    }

    [Fact]
    public void PinnedImagesStayAndStillCountTowardsTheSize()
    {
        LibraryEntry[] entries = [Entry('a', 100, idleDays: 90), Entry('b', 100, idleDays: 80), Entry('c', 100, idleDays: 1)];
        var pinned = new HashSet<string> { Sha('a') };

        var plan = Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 200, Pinned = pinned });
        Assert.Equal(["b"], Removed(plan));

        var tight = Plan(entries, new LibraryCleanupPolicy { MaxTotalBytes = 50, MaxIdle = TimeSpan.FromDays(30), Pinned = pinned });
        Assert.Equal(["b", "c"], Removed(tight));
        Assert.Equal(100, tight.BytesRemaining);
    }

    [Fact]
    public void OnlyTheNewestVersionsOfAProductAreKept()
    {
        LibraryEntry[] entries =
        [
            Entry('a', product: "p", version: "9.0"),
            Entry('b', product: "p", version: "10.0"),
            Entry('c', product: "p", version: "10.1"),
            Entry('d', product: "p", version: "2.0"),
        ];

        Assert.Equal(["a", "d"], Removed(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 2 })));
        Assert.Equal(["a", "b", "d"], Removed(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 1 })));
        Assert.Empty(Removed(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 4 })));
        Assert.All(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 1 }).Items, i => Assert.Equal(LibraryCleanupReason.SupersededVersion, i.Reason));
    }

    [Fact]
    public void VersionsAreComparedPerProductArchitectureAndLanguage()
    {
        LibraryEntry[] entries =
        [
            Entry('a', product: "p", version: "1.0", architecture: "x64"),
            Entry('b', product: "p", version: "2.0", architecture: "x64"),
            Entry('c', product: "p", version: "1.0", architecture: "arm64"),
            Entry('d', product: "p", version: "1.0", architecture: "x64", language: "de"),
            Entry('e', product: "q", version: "1.0", architecture: "x64"),
        ];

        Assert.Equal(["a"], Removed(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 1 })));
    }

    [Fact]
    public void ImagesOfTheSameVersionAreNotEachOthersSuccessors()
    {
        LibraryEntry[] entries = [Entry('a', product: "p", version: "2.6.2"), Entry('b', product: "p", version: "2.6.2"), Entry('c', product: "p", version: "2.5.0")];

        Assert.Equal(["c"], Removed(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 1 })));
    }

    [Fact]
    public void ImagesThatDoNotNameTheirProductAndVersionAreNeverSuperseded()
    {
        LibraryEntry[] entries = [Entry('a'), Entry('b', product: "p"), Entry('c', version: "1.0"), Entry('d', product: "p", version: "3.0")];

        Assert.Empty(Removed(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 1 })));
    }

    [Fact]
    public void APinnedOldVersionIsNeverSuperseded()
    {
        LibraryEntry[] entries = [Entry('a', product: "p", version: "1.0"), Entry('b', product: "p", version: "2.0"), Entry('c', product: "p", version: "3.0")];

        Assert.Equal(["a", "b"], Removed(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 1 })));
        Assert.Equal(["b"], Removed(Plan(entries, new LibraryCleanupPolicy { KeepVersionsPerProduct = 1, Pinned = new HashSet<string> { Sha('a') } })));
    }

    [Fact]
    public void LocalCopiesOfWhatTheShareHasAreRemovedOnlyWhenAskedFor()
    {
        LibraryEntry[] entries = [Entry('a'), Entry('b'), Entry('c')];

        Assert.Empty(Removed(Plan(entries, new LibraryCleanupPolicy(), 'a', 'z')));

        var plan = Plan(entries, new LibraryCleanupPolicy { RemoveCopiesAlsoOnShare = true }, 'a', 'z');
        Assert.Equal(["a"], Removed(plan));
        Assert.Equal(LibraryCleanupReason.ExistsOnShare, plan.Items[0].Reason);

        var pinned = Plan(entries, new LibraryCleanupPolicy { RemoveCopiesAlsoOnShare = true, Pinned = new HashSet<string> { Sha('a') } }, 'a');
        Assert.Empty(pinned.Items);
    }

    [Fact]
    public void AnImageKeepsTheFirstReasonItWasMarkedFor()
    {
        LibraryEntry[] entries =
        [
            Entry('a', idleDays: 100, product: "p", version: "1.0"),
            Entry('b', idleDays: 100, product: "p", version: "2.0"),
            Entry('c', idleDays: 100),
            Entry('d', idleDays: 1),
        ];

        var plan = Plan(entries, new LibraryCleanupPolicy { RemoveCopiesAlsoOnShare = true, KeepVersionsPerProduct = 1, MaxIdle = TimeSpan.FromDays(30), MaxTotalBytes = 0 }, 'b');

        Assert.Equal(
            [("a", LibraryCleanupReason.SupersededVersion), ("b", LibraryCleanupReason.ExistsOnShare), ("c", LibraryCleanupReason.NotUsedForLong), ("d", LibraryCleanupReason.OverQuota)],
            plan.Items.Select(i => (i.Entry.Sha256[..1], i.Reason)));
    }

    // --- through the library, with real files ---

    private async Task<(ImageLibrary Library, string[] Hashes)> ThreeImages(int sizeEach = 10_000)
    {
        var library = _fx.Library();
        var hashes = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var (path, sha) = _fx.File($"{i}.iso", sizeEach, seed: 100 + i);
            await library.AddAsync(path, new LibraryImageInfo { Name = $"Image {i}", CatalogId = "p", Version = $"{i + 1}.0" }, LibraryAddMode.Move);
            hashes.Add(sha);
            _fx.Clock.Advance(TimeSpan.FromDays(20));
        }

        return (library, [.. hashes]);
    }

    [Fact]
    public async Task PreviewDeletesNothingAndRunningItRemovesExactlyThePlannedImages()
    {
        var (library, hashes) = await ThreeImages();
        // Now is 60 days after the first image was added: it is the only one idle for more than 45 days.
        var policy = new LibraryCleanupPolicy { MaxIdle = TimeSpan.FromDays(45) };

        var plan = await library.PlanCleanupAsync(policy, CancellationToken.None);

        Assert.Equal([hashes[0]], plan.Items.Select(i => i.Entry.Sha256));
        Assert.Equal(10_000, plan.BytesToFree);
        Assert.Equal(3, (await library.ListAsync(CancellationToken.None)).Entries.Count);
        Assert.Equal(6, Directory.GetFiles(_fx.Local).Length);

        var result = await library.CleanupAsync(plan, CancellationToken.None);

        Assert.Equal([hashes[0]], result.Removed.Select(e => e.Sha256));
        Assert.Empty(result.Failed);
        Assert.Equal(10_000, result.BytesFreed);
        Assert.Equal(
            new[] { hashes[1], hashes[2] }.Order(StringComparer.Ordinal),
            (await library.ListAsync(CancellationToken.None)).Entries.Select(e => e.Sha256).Order(StringComparer.Ordinal));
        Assert.Equal(4, Directory.GetFiles(_fx.Local).Length);
    }

    [Fact]
    public async Task UsingAnImageProtectsItFromTheIdleLimit()
    {
        var (library, hashes) = await ThreeImages();
        await library.TouchAsync(hashes[0], CancellationToken.None);

        var plan = await library.PlanCleanupAsync(new LibraryCleanupPolicy { MaxIdle = TimeSpan.FromDays(35) }, CancellationToken.None);

        Assert.Equal([hashes[1]], plan.Items.Select(i => i.Entry.Sha256));
    }

    [Fact]
    public async Task TheSizeLimitAndVersionLimitWorkOnRealImages()
    {
        var (library, hashes) = await ThreeImages();

        var bySize = await library.PlanCleanupAsync(new LibraryCleanupPolicy { MaxTotalBytes = 20_000 }, CancellationToken.None);
        Assert.Equal([hashes[0]], bySize.Items.Select(i => i.Entry.Sha256));

        var byVersion = await library.PlanCleanupAsync(new LibraryCleanupPolicy { KeepVersionsPerProduct = 1 }, CancellationToken.None);
        Assert.Equal([hashes[0], hashes[1]], byVersion.Items.Select(i => i.Entry.Sha256).OrderBy(h => Array.IndexOf(hashes, h)));
    }

    [Fact]
    public async Task ACleanupSkipsWhatIsGoneOrDamagedAndReportsWhatChangedSinceThePreview()
    {
        var (library, hashes) = await ThreeImages();
        var plan = await library.PlanCleanupAsync(new LibraryCleanupPolicy { MaxTotalBytes = 0 }, CancellationToken.None);

        // Image 0 is rewritten as a different, valid entry of the same address; image 1 is deleted by someone else.
        File.WriteAllBytes(LibraryFixture.ImagePath(_fx.Local, hashes[0]), new byte[77]);
        File.WriteAllText(
            LibraryFixture.MetadataPath(_fx.Local, hashes[0]),
            $$"""{ "schema": 1, "sha256": "{{hashes[0]}}", "file": "{{hashes[0]}}.iso", "size": 77, "downloadedUtc": "2026-10-01T12:00:00Z", "lastUsedUtc": "2026-10-01T12:00:00Z" }""");
        File.Delete(LibraryFixture.ImagePath(_fx.Local, hashes[1]));

        var result = await library.CleanupAsync(plan, CancellationToken.None);

        Assert.Equal([hashes[2]], result.Removed.Select(e => e.Sha256));
        var failure = Assert.Single(result.Failed);
        Assert.Equal(hashes[0], failure.Entry.Sha256);
        Assert.Contains("changed after the preview", failure.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(LibraryFixture.ImagePath(_fx.Local, hashes[0])));
    }

    [Fact]
    public async Task AnImageThatIsDamagedIsLeftToRepairInsteadOfBeingDeletedByCleanup()
    {
        var (library, hashes) = await ThreeImages();
        var plan = await library.PlanCleanupAsync(new LibraryCleanupPolicy { MaxTotalBytes = 0 }, CancellationToken.None);
        File.WriteAllBytes(LibraryFixture.ImagePath(_fx.Local, hashes[0]), new byte[10]);

        var result = await library.CleanupAsync(plan, CancellationToken.None);

        Assert.Equal(2, result.Removed.Count);
        Assert.Empty(result.Failed);
        Assert.True(File.Exists(LibraryFixture.ImagePath(_fx.Local, hashes[0])));
    }

    [Fact]
    public async Task CopiesThatTheShareHasAreRemovedLocallyAndStayOnTheShare()
    {
        var (a, shaA) = _fx.File("a.iso", seed: 1);
        var (b, shaB) = _fx.File("b.iso", seed: 2);
        await _fx.Colleague().AddAsync(a, new LibraryImageInfo());
        var library = _fx.Library(withShared: true);
        await library.AddAsync(a, new LibraryImageInfo());
        await library.AddAsync(b, new LibraryImageInfo());

        var plan = await library.PlanCleanupAsync(new LibraryCleanupPolicy { RemoveCopiesAlsoOnShare = true }, CancellationToken.None);
        await library.CleanupAsync(plan, CancellationToken.None);

        var entries = (await library.ListAsync(CancellationToken.None)).Entries;
        Assert.Contains(entries, e => e.Sha256 == shaA && e.Location == LibraryLocation.Shared);
        Assert.DoesNotContain(entries, e => e.Sha256 == shaA && e.Location == LibraryLocation.Local);
        Assert.Contains(entries, e => e.Sha256 == shaB && e.Location == LibraryLocation.Local);
    }

    [Fact]
    public async Task LimitsThatMakeNoSenseAreRefused()
    {
        var library = _fx.Library();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => library.PlanCleanupAsync(new LibraryCleanupPolicy { MaxTotalBytes = -1 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => library.PlanCleanupAsync(new LibraryCleanupPolicy { KeepVersionsPerProduct = 0 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => library.PlanCleanupAsync(new LibraryCleanupPolicy { MaxIdle = TimeSpan.FromDays(-1) }, CancellationToken.None));
    }
}
