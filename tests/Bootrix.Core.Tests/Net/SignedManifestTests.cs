// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Net;

public sealed class SignedManifestTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly ManifestSigner _current = new("2026-a");
    private readonly ManifestSigner _next = new("2027-a");
    private readonly InMemoryVersionStore _store = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public void Dispose()
    {
        _current.Dispose();
        _next.Dispose();
    }

    private SignedManifestVerifier Verifier(params ManifestSigner[] trusted) =>
        new((trusted.Length == 0 ? [_current] : trusted).Select(s => s.PublicKey), _store, _clock);

    private static BootrixException Rejected(Action action, string detailPart)
    {
        var ex = Assert.Throws<BootrixException>(action);
        Assert.Equal(ErrorCode.SignatureInvalid, ex.Code);
        Assert.Contains(detailPart, ex.Detail, StringComparison.OrdinalIgnoreCase);
        return ex;
    }

    [Fact]
    public void ValidManifestIsAcceptedWithAllItsContent()
    {
        var artifacts = """[{ "name": "linux.json", "url": "https://catalog.example.org/linux.json", "size": 100, "sha256": "3a4c9877b483ab46d7c3fbe165a0db275e1ae3cfe56a5657e5a47c2f99a99d1e" }]""";
        var envelope = _current.Sign(ManifestSigner.Manifest(version: 17, artifacts: artifacts));

        var manifest = Verifier().Verify(envelope, "catalog");

        Assert.Equal("catalog", manifest.Channel);
        Assert.Equal(17, manifest.Version);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), manifest.IssuedUtc);
        Assert.Equal("2026-a", manifest.SignedByKeyId);
        Assert.Equal(["ubuntu", "debian"], manifest.ReadPayload<Dictionary<string, string[]>>()!["distributions"]);

        var artifact = Assert.Single(manifest.Artifacts);
        Assert.Equal("linux.json", artifact.Name);
        Assert.Equal(new Uri("https://catalog.example.org/linux.json"), artifact.Url);
        Assert.Equal(100, artifact.Size);
        Assert.Same(artifact, manifest.FindArtifact("linux.json"));
        Assert.Null(manifest.FindArtifact("other.json"));
    }

    [Fact]
    public void PayloadCanBeAnyJsonAndMayBeMissing()
    {
        var withoutPayload = ManifestSigner.Manifest().Replace("\"payload\": { \"distributions\": [\"ubuntu\", \"debian\"] },", string.Empty, StringComparison.Ordinal);

        var manifest = Verifier().Verify(_current.Sign(withoutPayload), "catalog");

        Assert.Null(manifest.ReadPayload<Dictionary<string, string>>());
    }

    [Fact]
    public void KeyRotationNextKeyIsAcceptedAlongsideTheCurrentOne()
    {
        var verifier = Verifier(_current, _next);

        Assert.Equal("2026-a", verifier.Verify(_current.Sign(ManifestSigner.Manifest(version: 1)), "catalog").SignedByKeyId);
        Assert.Equal("2027-a", verifier.Verify(_next.Sign(ManifestSigner.Manifest(version: 2)), "catalog").SignedByKeyId);
    }

    [Fact]
    public void UnknownKeyIsRejected()
    {
        using var stranger = new ManifestSigner("stranger");

        Rejected(() => Verifier().Verify(stranger.Sign(ManifestSigner.Manifest()), "catalog"), "not trusted");
    }

    [Fact]
    public void SignatureFromAnotherKeyUnderATrustedNameIsRejected()
    {
        using var impostor = new ManifestSigner("2026-a");

        Rejected(() => Verifier().Verify(impostor.Sign(ManifestSigner.Manifest()), "catalog"), "signature does not match");
    }

    [Fact]
    public void KeyThatIsNotInTheEmbeddedSetCannotBeNamedByTheEnvelope()
    {
        var envelope = _next.Sign(ManifestSigner.Manifest());

        Rejected(() => Verifier(_current).Verify(envelope, "catalog"), "not trusted");
    }

    [Fact]
    public void ChangedSignedBytesBreakTheSignature()
    {
        var manifest = Encoding.UTF8.GetBytes(ManifestSigner.Manifest(version: 5));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var verifier = new SignedManifestVerifier([new ManifestPublicKey("k", key.ExportSubjectPublicKeyInfo())], _store, _clock);
        var signature = key.SignData(manifest, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence);
        manifest[manifest.Length / 2] ^= 1;

        Rejected(() => verifier.Verify(ManifestSigner.Envelope(manifest, signature, "k"), "catalog"), "signature does not match");
    }

    [Fact]
    public void ChangedVersionOrExpiryInTheSignedTextIsDetected()
    {
        var original = ManifestSigner.Manifest(version: 3);
        var envelope = _current.Sign(original);
        var forged = _current.Sign(original.Replace("\"version\": 3", "\"version\": 4", StringComparison.Ordinal));

        // Taking the signature of the first and the signed bytes of the second must not verify.
        var swapped = ManifestSigner.Envelope(
            Convert.FromBase64String(ExtractField(forged, "signed")),
            Convert.FromBase64String(ExtractField(envelope, "signature")),
            "2026-a");

        Rejected(() => Verifier().Verify(swapped, "catalog"), "signature does not match");
    }

    [Fact]
    public void SignatureInP1363FormatIsNotAccepted()
    {
        var manifest = Encoding.UTF8.GetBytes(ManifestSigner.Manifest());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var verifier = new SignedManifestVerifier([new ManifestPublicKey("k", key.ExportSubjectPublicKeyInfo())], _store, _clock);
        var raw = key.SignData(manifest, HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Rejected(() => verifier.Verify(ManifestSigner.Envelope(manifest, raw, "k"), "catalog"), "signature does not match");
    }

    [Fact]
    public void SignatureOverAnotherHashIsNotAccepted()
    {
        var manifest = Encoding.UTF8.GetBytes(ManifestSigner.Manifest());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var verifier = new SignedManifestVerifier([new ManifestPublicKey("k", key.ExportSubjectPublicKeyInfo())], _store, _clock);
        var weak = key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Rejected(() => verifier.Verify(ManifestSigner.Envelope(manifest, weak, "k"), "catalog"), "signature does not match");
    }

    [Fact]
    public void ExpiredManifestIsRejectedFromTheMomentOfExpiry()
    {
        var envelope = _current.Sign(ManifestSigner.Manifest(expires: "2026-10-15T00:00:00Z"));

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 14, 23, 59, 59, TimeSpan.Zero));
        Assert.Equal(1, Verifier().Verify(envelope, "catalog").Version);

        _clock.SetUtcNow(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        Rejected(() => Verifier().Verify(envelope, "catalog"), "expired");
    }

    [Fact]
    public void ValidityPeriodThatEndsBeforeItStartsIsRejected()
    {
        var envelope = _current.Sign(ManifestSigner.Manifest(issued: "2026-10-10T00:00:00Z", expires: "2026-10-05T00:00:00Z"));

        Rejected(() => Verifier().Verify(envelope, "catalog"), "not plausible");
    }

    [Fact]
    public void OlderVersionThanAlreadySeenIsARollback()
    {
        var verifier = Verifier();
        verifier.Verify(_current.Sign(ManifestSigner.Manifest(version: 10)), "catalog");

        Rejected(() => verifier.Verify(_current.Sign(ManifestSigner.Manifest(version: 9)), "catalog"), "rollback");
    }

    [Fact]
    public void SameVersionCanBeVerifiedAgain()
    {
        var verifier = Verifier();
        var envelope = _current.Sign(ManifestSigner.Manifest(version: 10));

        verifier.Verify(envelope, "catalog");
        verifier.Verify(envelope, "catalog");

        Assert.Equal(1, _store.Writes);
    }

    [Fact]
    public void AcceptedNewerVersionRaisesTheFloorAndRejectedOnesDoNot()
    {
        var verifier = Verifier();
        verifier.Verify(_current.Sign(ManifestSigner.Manifest(version: 5)), "catalog");

        Assert.Throws<BootrixException>(() => verifier.Verify(_current.Sign(ManifestSigner.Manifest(version: 99, expires: "2026-10-01T12:00:00Z")), "catalog"));
        Assert.Equal(5, _store.GetHighestVersion("catalog"));

        verifier.Verify(_current.Sign(ManifestSigner.Manifest(version: 6)), "catalog");
        Assert.Equal(6, _store.GetHighestVersion("catalog"));
    }

    [Fact]
    public void VersionsAreCountedPerChannel()
    {
        var verifier = Verifier();
        verifier.Verify(_current.Sign(ManifestSigner.Manifest(channel: "revocation", version: 50)), "revocation");

        var catalog = verifier.Verify(_current.Sign(ManifestSigner.Manifest(channel: "catalog", version: 1)), "catalog");

        Assert.Equal(1, catalog.Version);
    }

    [Fact]
    public void DocumentOfAnotherChannelIsRejectedEvenWithAValidSignature()
    {
        var envelope = _current.Sign(ManifestSigner.Manifest(channel: "revocation"));

        Rejected(() => Verifier().Verify(envelope, "catalog"), "channel");
        Assert.Equal(0, _store.GetHighestVersion("catalog"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"format\": \"bootrix-manifest-1\" }")]
    [InlineData("{ \"format\": \"bootrix-manifest-2\", \"algorithm\": \"ecdsa-p384-sha384-der\", \"keyId\": \"2026-a\", \"signed\": \"AA==\", \"signature\": \"AA==\" }")]
    [InlineData("{ \"format\": \"bootrix-manifest-1\", \"algorithm\": \"none\", \"keyId\": \"2026-a\", \"signed\": \"AA==\", \"signature\": \"AA==\" }")]
    [InlineData("{ \"format\": \"bootrix-manifest-1\", \"algorithm\": \"ecdsa-p384-sha384-der\", \"keyId\": \"2026-a\", \"signed\": \"!!!\", \"signature\": \"AA==\" }")]
    [InlineData("{ \"format\": \"bootrix-manifest-1\", \"algorithm\": \"ecdsa-p384-sha384-der\", \"keyId\": \"2026-a\", \"signed\": \"AA==\", \"signature\": \"AA==\" }")]
    public void MalformedEnvelopesAreRejectedAsInvalidSignature(string envelope)
    {
        var ex = Assert.Throws<BootrixException>(() => Verifier().Verify(Encoding.UTF8.GetBytes(envelope), "catalog"));

        Assert.Equal(ErrorCode.SignatureInvalid, ex.Code);
    }

    [Fact]
    public void ValidlySignedGarbageIsRejectedAfterTheSignatureCheck()
    {
        foreach (var signed in new[] { "not json", "[]", "{}", "{ \"format\": 1, \"channel\": \"catalog\" }", "{ \"format\": 1, \"channel\": \"catalog\", \"version\": \"x\", \"issuedUtc\": \"2026-10-01T00:00:00Z\", \"expiresUtc\": \"2026-10-02T00:00:00Z\" }" })
        {
            var ex = Assert.Throws<BootrixException>(() => Verifier().Verify(_current.Sign(signed), "catalog"));

            Assert.Equal(ErrorCode.SignatureInvalid, ex.Code);
        }
    }

    [Fact]
    public void FutureManifestFormatIsRejected()
    {
        var envelope = _current.Sign(ManifestSigner.Manifest().Replace("\"format\": 1", "\"format\": 2", StringComparison.Ordinal));

        Rejected(() => Verifier().Verify(envelope, "catalog"), "format");
    }

    [Fact]
    public void OversizedEnvelopeIsRejectedWithoutParsing()
    {
        var huge = new byte[17 * 1024 * 1024];

        Rejected(() => Verifier().Verify(huge, "catalog"), "large");
    }

    [Theory]
    [InlineData("\"name\": \"../etc/passwd\"")]
    [InlineData("\"name\": \"dir/file\"")]
    [InlineData("\"name\": \"\"")]
    [InlineData("\"name\": \"a\\\\b\"")]
    public void ArtifactNamesMustBePlainFileNames(string name)
    {
        var artifacts = $$"""[{ {{name}}, "size": 1, "sha256": "{{new string('a', 64)}}" }]""";

        Rejected(() => Verifier().Verify(_current.Sign(ManifestSigner.Manifest(artifacts: artifacts)), "catalog"), "artifact name");
    }

    [Theory]
    [InlineData("\"size\": -1, \"sha256\": \"{0}\"")]
    [InlineData("\"size\": 1, \"sha256\": \"abc\"")]
    [InlineData("\"size\": 1, \"sha256\": \"{1}\"")]
    public void ArtifactsNeedASizeAndAWellFormedSha256(string fields)
    {
        var artifacts = "[{ \"name\": \"a.bin\", " + string.Format(System.Globalization.CultureInfo.InvariantCulture, fields, new string('a', 64), new string('z', 64)) + " }]";

        Rejected(() => Verifier().Verify(_current.Sign(ManifestSigner.Manifest(artifacts: artifacts)), "catalog"), "artifact 'a.bin'");
    }

    [Fact]
    public void RepeatedArtifactNamesAreRejected()
    {
        var one = $$"""{ "name": "a.bin", "size": 1, "sha256": "{{new string('a', 64)}}" }""";

        Rejected(() => Verifier().Verify(_current.Sign(ManifestSigner.Manifest(artifacts: $"[{one}, {one}]")), "catalog"), "repeated");
    }

    [Theory]
    [InlineData("ftp://example.org/a.bin")]
    [InlineData("file:///etc/passwd")]
    [InlineData("a/relative/path")]
    public void ArtifactAddressMustBeAWebAddress(string url)
    {
        var artifacts = $$"""[{ "name": "a.bin", "url": "{{url}}", "size": 1, "sha256": "{{new string('a', 64)}}" }]""";

        var ex = Assert.Throws<BootrixException>(() => Verifier().Verify(_current.Sign(ManifestSigner.Manifest(artifacts: artifacts)), "catalog"));

        Assert.Equal(ErrorCode.SignatureInvalid, ex.Code);
    }

    [Fact]
    public void VerifierNeedsAtLeastOneKey()
    {
        Assert.Throws<ArgumentException>(() => new SignedManifestVerifier([], _store));
    }

    [Fact]
    public void OnlyP384KeysCanBeTrusted()
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var rsa = RSA.Create(2048);

        Assert.Throws<ArgumentException>(() => new ManifestPublicKey("p256", p256.ExportSubjectPublicKeyInfo()));
        Assert.ThrowsAny<Exception>(() => new ManifestPublicKey("rsa", rsa.ExportSubjectPublicKeyInfo()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("quo\"te")]
    public void KeyIdsAreRestrictedToSafeCharacters(string keyId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.ThrowsAny<ArgumentException>(() => new ManifestPublicKey(keyId, key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public async Task ArtifactBecomesADownloadThatChecksSizeAndDigest()
    {
        var content = DownloadTestSupport.RandomBytes(1_500_000);
        await using var server = await FileServer.StartAsync(content);
        var artifacts = $$"""[{ "name": "linux.json", "url": "{{server.FileUri}}", "size": {{content.Length}}, "sha256": "{{DownloadTestSupport.Sha256Hex(content)}}" }]""";
        var manifest = Verifier().Verify(_current.Sign(ManifestSigner.Manifest(artifacts: artifacts)), "catalog");
        using var dir = new TempDirectory();

        var request = manifest.FindArtifact("linux.json")!.ToRequest(DownloadTestSupport.FastOptions());
        var result = await DownloadTestSupport.Downloader().DownloadAsync(request, dir.File("linux.json"));

        Assert.Equal(DownloadTestSupport.Sha256Hex(content), result.Sha256);

        server.Content = DownloadTestSupport.RandomBytes(content.Length, seed: 77);
        var ex = await Assert.ThrowsAsync<BootrixException>(() => DownloadTestSupport.Downloader().DownloadAsync(request, dir.File("again.json")));
        Assert.Equal(ErrorCode.DownloadHashMismatch, ex.Code);
    }

    [Fact]
    public void ArtifactWithoutAddressCannotBeDownloaded()
    {
        var artifact = new ManifestArtifact { Name = "a", Size = 1, Sha256 = new string('a', 64) };

        Assert.Throws<InvalidOperationException>(() => artifact.ToRequest());
    }

    private static string ExtractField(byte[] envelope, string name)
    {
        using var document = System.Text.Json.JsonDocument.Parse(envelope);
        return document.RootElement.GetProperty(name).GetString()!;
    }
}
