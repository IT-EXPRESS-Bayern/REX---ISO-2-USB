// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using Bootrix.Core.Catalog.Rescue;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Rescue;

public sealed class RescueCatalogUpdateTests : IDisposable
{
    private static readonly Uri ManifestUrl = new("https://catalog.example.org/rescue.json");

    private readonly ManifestSigner _signer = new("rescue-a");
    private readonly InMemoryVersionStore _versions = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
    private readonly TempDirectory _cache = new();

    public void Dispose()
    {
        _signer.Dispose();
        _cache.Dispose();
    }

    private string CacheFile => Path.Combine(_cache.Path, "rescue-catalog.signed.json");

    private RescueCatalogStore NewStore() => new(
        _cache.Path,
        new SignedManifestVerifier([_signer.PublicKey], _versions, _clock),
        NullLogger<RescueCatalogStore>.Instance,
        _clock);

    private byte[] Envelope(long manifestVersion, long catalogVersion, string channel = "rescue-catalog", string expires = "2026-10-15T00:00:00Z", string? payload = null) =>
        _signer.Sign(ManifestSigner.Manifest(
            channel: channel,
            version: manifestVersion,
            expires: expires,
            payload: payload ?? RescueFixtures.EmbeddedWithVersion(catalogVersion)));

    private static RescueCatalogUpdater Updater(RescueCatalogStore store, Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new RescueCatalogUpdater(store, new HttpClient(handler), ManifestUrl, NullLogger<RescueCatalogUpdater>.Instance);
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static byte[] Tampered(byte[] envelope)
    {
        var text = Encoding.UTF8.GetString(envelope);
        const string marker = "\"signed\": \"";
        var at = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;

        // The first Base64 character of '{' is 'e'; 'f' decodes to different bytes but is still valid Base64.
        return Encoding.UTF8.GetBytes(string.Concat(text.AsSpan(0, at), "f", text.AsSpan(at + 1)));
    }

    private static BootrixException Failed(Action action, ErrorCode code, string detailPart)
    {
        var ex = Assert.Throws<BootrixException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Contains(detailPart, ex.Detail, StringComparison.OrdinalIgnoreCase);
        return ex;
    }

    [Fact]
    public void WithoutAnyCacheTheEmbeddedCatalogIsInEffect()
    {
        var snapshot = NewStore().Current;

        Assert.Equal(RescueCatalogOrigin.Embedded, snapshot.Origin);
        Assert.Equal(1, snapshot.Document.Version);
        Assert.False(snapshot.IsStale);
        Assert.False(File.Exists(CacheFile));
    }

    [Fact]
    public async Task NewerSignedCatalogIsStoredAndStillUsedAfterARestart()
    {
        var store = NewStore();
        var updater = Updater(store, _ => Ok(Envelope(1, 7)), out var handler);

        var result = await updater.UpdateAsync(CancellationToken.None);

        Assert.Equal(new RescueCatalogUpdate(RescueCatalogUpdateStatus.Updated, 7), result);
        Assert.Equal(ManifestUrl, Assert.Single(handler.Calls).Uri);
        Assert.Equal((RescueCatalogOrigin.Cache, 7), (store.Current.Origin, store.Current.Document.Version));
        Assert.True(File.Exists(CacheFile));

        var afterRestart = NewStore().Current;
        Assert.Equal((RescueCatalogOrigin.Cache, 7), (afterRestart.Origin, afterRestart.Document.Version));
    }

    [Fact]
    public void ACatalogThatIsNotNewerThanTheEmbeddedOneIsNotStored()
    {
        var store = NewStore();

        var result = store.Apply(Envelope(1, 1));

        Assert.Equal(new RescueCatalogUpdate(RescueCatalogUpdateStatus.UpToDate, 1), result);
        Assert.Equal(RescueCatalogOrigin.Embedded, store.Current.Origin);
        Assert.False(File.Exists(CacheFile));
    }

    [Fact]
    public void ACatalogThatIsNotNewerThanTheCachedOneIsNotStoredEvenWithAHigherManifestVersion()
    {
        var store = NewStore();
        store.Apply(Envelope(1, 7));
        var before = File.ReadAllBytes(CacheFile);

        var result = store.Apply(Envelope(2, 5));

        Assert.Equal(new RescueCatalogUpdate(RescueCatalogUpdateStatus.UpToDate, 7), result);
        Assert.Equal(before, File.ReadAllBytes(CacheFile));
    }

    [Fact]
    public void ExpiredManifestIsRejectedAndNothingChanges()
    {
        var store = NewStore();
        var envelope = Envelope(1, 7, expires: "2026-10-03T00:00:00Z");
        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));

        Failed(() => store.Apply(envelope), ErrorCode.SignatureInvalid, "expired");

        Assert.Equal(RescueCatalogOrigin.Embedded, store.Current.Origin);
        Assert.False(File.Exists(CacheFile));
    }

    [Fact]
    public void RollbackToAnOlderManifestIsRejected()
    {
        var store = NewStore();
        store.Apply(Envelope(10, 7));

        Failed(() => store.Apply(Envelope(9, 8)), ErrorCode.SignatureInvalid, "rollback");

        Assert.Equal(7, store.Current.Document.Version);
    }

    [Fact]
    public void TamperedDocumentIsRejected()
    {
        var store = NewStore();

        Failed(() => store.Apply(Tampered(Envelope(1, 7))), ErrorCode.SignatureInvalid, "signature does not match");

        Assert.False(File.Exists(CacheFile));
    }

    [Fact]
    public void DocumentSignedByAnUntrustedKeyIsRejected()
    {
        using var stranger = new ManifestSigner("stranger");
        var envelope = stranger.Sign(ManifestSigner.Manifest(channel: "rescue-catalog", payload: RescueFixtures.EmbeddedWithVersion(7)));

        Failed(() => NewStore().Apply(envelope), ErrorCode.SignatureInvalid, "not trusted");
    }

    [Fact]
    public void ADocumentOfAnotherChannelIsRejected()
    {
        Failed(() => NewStore().Apply(Envelope(1, 7, channel: "catalog")), ErrorCode.SignatureInvalid, "channel");
    }

    [Fact]
    public void SignedButMalformedCatalogIsRejectedAndRaisesTheRollbackFloor()
    {
        var store = NewStore();
        var envelope = Envelope(5, 7, payload: "{ \"schemaVersion\": 9 }");

        Failed(() => store.Apply(envelope), ErrorCode.CatalogUnavailable, "schema version 9");

        Assert.False(File.Exists(CacheFile));
        Assert.Equal(RescueCatalogOrigin.Embedded, store.Current.Origin);

        // The verifier has already accepted manifest 5, so the corrected catalog has to be published as 6 or higher.
        Failed(() => store.Apply(Envelope(4, 7)), ErrorCode.SignatureInvalid, "rollback");
        Assert.Equal(7, store.Apply(Envelope(6, 7)).Version);
    }

    [Fact]
    public async Task ServerErrorAndNetworkFailureAreReportedAsCatalogUnavailable()
    {
        var store = NewStore();

        var serverError = Updater(store, _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), out _);
        Assert.Equal(ErrorCode.CatalogUnavailable, (await Assert.ThrowsAsync<BootrixException>(() => serverError.UpdateAsync(CancellationToken.None))).Code);

        var unreachable = Updater(store, _ => throw new HttpRequestException("no route"), out _);
        var ex = await Assert.ThrowsAsync<BootrixException>(() => unreachable.UpdateAsync(CancellationToken.None));
        Assert.Equal(ErrorCode.CatalogUnavailable, ex.Code);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task OversizedDocumentIsRefusedWithAndWithoutContentLength()
    {
        var store = NewStore();

        var announced = Updater(store, _ => Ok(new byte[5 * 1024 * 1024]), out _);
        Assert.Contains("unreasonably large", (await Assert.ThrowsAsync<BootrixException>(() => announced.UpdateAsync(CancellationToken.None))).Detail, StringComparison.Ordinal);

        var endless = Updater(store, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) }, out _);
        Assert.Contains("unreasonably large", (await Assert.ThrowsAsync<BootrixException>(() => endless.UpdateAsync(CancellationToken.None))).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsAFailure()
    {
        using var cts = new CancellationTokenSource();
        var updater = Updater(NewStore(), _ => throw new OperationCanceledException(cts.Token), out _);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.UpdateAsync(cts.Token));
    }

    [Fact]
    public void UpdaterOnlyTalksHttps()
    {
        var store = NewStore();

        Assert.Throws<ArgumentException>(() => new RescueCatalogUpdater(store, new HttpClient(), new Uri("http://catalog.example.org/rescue.json"), NullLogger<RescueCatalogUpdater>.Instance));
        Assert.Throws<ArgumentException>(() => new RescueCatalogUpdater(store, new HttpClient(), new Uri("catalog/rescue.json", UriKind.Relative), NullLogger<RescueCatalogUpdater>.Instance));
    }

    [Fact]
    public void CacheThatWasTamperedWithOnDiskIsIgnoredAtTheNextStart()
    {
        NewStore().Apply(Envelope(1, 7));
        File.WriteAllBytes(CacheFile, Tampered(File.ReadAllBytes(CacheFile)));

        var snapshot = NewStore().Current;

        Assert.Equal((RescueCatalogOrigin.Embedded, 1), (snapshot.Origin, snapshot.Document.Version));
    }

    [Fact]
    public void CacheWhoseManifestHasExpiredFallsBackToTheEmbeddedCatalog()
    {
        NewStore().Apply(Envelope(1, 7, expires: "2026-10-15T00:00:00Z"));
        Assert.Equal(RescueCatalogOrigin.Cache, NewStore().Current.Origin);

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(RescueCatalogOrigin.Embedded, NewStore().Current.Origin);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"format\": \"bootrix-manifest-1\" }")]
    public void UnusableCacheFilesAreIgnored(string content)
    {
        File.WriteAllText(CacheFile, content);

        Assert.Equal(RescueCatalogOrigin.Embedded, NewStore().Current.Origin);
    }

    [Fact]
    public void CacheThatHoldsAnOlderOrEqualCatalogThanTheBuildLosesToTheEmbeddedOne()
    {
        // What remains in the cache after Bootrix itself was updated to a build with a newer embedded catalog.
        File.WriteAllBytes(CacheFile, Envelope(1, 1));

        var snapshot = NewStore().Current;

        Assert.Equal(RescueCatalogOrigin.Embedded, snapshot.Origin);
    }

    [Fact]
    public void ACacheThatIsADirectoryInsteadOfAFileDoesNotBreakTheStart()
    {
        Directory.CreateDirectory(CacheFile);

        Assert.Equal(RescueCatalogOrigin.Embedded, NewStore().Current.Origin);
    }

    [Fact]
    public void StaleFlagFollowsTheCatalogsOwnExpiryDate()
    {
        var store = NewStore();
        Assert.False(store.Current.IsStale);

        _clock.SetUtcNow(new DateTimeOffset(2027, 4, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.False(store.Reload().IsStale);

        _clock.SetUtcNow(new DateTimeOffset(2027, 4, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.True(store.Reload().IsStale);
        Assert.NotEmpty(store.Current.Document.Entries);
    }

    [Fact]
    public void ReloadPicksUpACatalogThatAnotherStoreWroteToTheSameFolder()
    {
        var first = NewStore();
        var second = NewStore();
        Assert.Equal(RescueCatalogOrigin.Embedded, first.Current.Origin);

        second.Apply(Envelope(1, 7));

        Assert.Equal(RescueCatalogOrigin.Embedded, first.Current.Origin);
        Assert.Equal(7, first.Reload().Document.Version);
    }

    [Fact]
    public void StoringLeavesNoTemporaryFilesBehind()
    {
        var store = NewStore();
        store.Apply(Envelope(1, 7));
        store.Apply(Envelope(2, 8));

        Assert.Equal([CacheFile], Directory.GetFiles(_cache.Path));
    }

    [Fact]
    public void AnOfflineCopyOfASignedCatalogCanBeAppliedWithoutTheNetwork()
    {
        var store = NewStore();
        var file = Path.Combine(_cache.Path, "from-usb-stick.json");
        File.WriteAllBytes(file, Envelope(1, 3));

        var result = store.Apply(File.ReadAllBytes(file));

        Assert.Equal(RescueCatalogUpdateStatus.Updated, result.Status);
        Assert.Equal(3, store.Current.Document.Version);
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
