// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;

namespace Bootrix.Core.Net;

/// <summary>
/// Accepts signed metadata (catalog, revocation lists, update information) and nothing else. A document is
/// handed out only if its signature verifies, it belongs to the expected channel, it has not expired and it is not
/// older than one already seen. Every failure is <see cref="ErrorCode.SignatureInvalid"/>; the detail says which check failed.
/// </summary>
/// <remarks>
/// The envelope carries the exact bytes that were signed (<c>signed</c>, base64), so no JSON is ever re-serialized
/// before checking the signature:
/// <code>{ "format": "bootrix-manifest-1", "algorithm": "ecdsa-p384-sha384-der", "keyId": "...", "signed": "...", "signature": "..." }</code>
/// The signature is ECDSA over P-384 with SHA-384, DER-encoded, which is what <c>openssl dgst -sha384 -sign</c> writes.
/// </remarks>
public sealed class SignedManifestVerifier
{
    public const string EnvelopeFormat = "bootrix-manifest-1";

    public const string SignatureAlgorithm = "ecdsa-p384-sha384-der";

    private const int MaxEnvelopeBytes = 16 * 1024 * 1024;

    private readonly Dictionary<string, ManifestPublicKey> _keys;
    private readonly IVersionStore _versions;
    private readonly TimeProvider _time;

    /// <param name="trustedKeys">The keys that may sign, normally the current one and the next one.</param>
    public SignedManifestVerifier(IEnumerable<ManifestPublicKey> trustedKeys, IVersionStore versions, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        ArgumentNullException.ThrowIfNull(versions);

        _keys = trustedKeys.ToDictionary(k => k.KeyId, StringComparer.Ordinal);
        if (_keys.Count == 0)
        {
            throw new ArgumentException("At least one trusted key is required.", nameof(trustedKeys));
        }

        _versions = versions;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Verifies the envelope and records the version as the new rollback floor.
    /// </summary>
    /// <param name="expectedChannel">The kind of document the caller wants; a signed document of another channel is rejected.</param>
    public SignedManifest Verify(ReadOnlySpan<byte> envelope, string expectedChannel)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedChannel);

        var signed = Authenticate(envelope, out var keyId);
        var manifest = Parse(signed);

        Validate(manifest, expectedChannel);

        var highest = _versions.GetHighestVersion(expectedChannel);
        if (manifest.Version < highest)
        {
            throw Fail($"version {manifest.Version} is older than the already seen {highest} (rollback)");
        }

        _versions.Record(expectedChannel, manifest.Version);
        return manifest with { SignedByKeyId = keyId };
    }

    private byte[] Authenticate(ReadOnlySpan<byte> envelope, out string keyId)
    {
        if (envelope.Length > MaxEnvelopeBytes)
        {
            throw Fail("the document is unreasonably large");
        }

        ManifestEnvelope? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ManifestEnvelope>(envelope, CoreJson.Options);
        }
        catch (JsonException ex)
        {
            throw Fail("the envelope is not valid JSON: " + ex.Message, ex);
        }

        if (parsed is null || string.IsNullOrEmpty(parsed.Signed) || string.IsNullOrEmpty(parsed.Signature) || string.IsNullOrEmpty(parsed.KeyId))
        {
            throw Fail("the envelope is incomplete");
        }

        if (parsed.Format != EnvelopeFormat || parsed.Algorithm != SignatureAlgorithm)
        {
            throw Fail($"unsupported envelope '{parsed.Format}' / algorithm '{parsed.Algorithm}'");
        }

        if (!_keys.TryGetValue(parsed.KeyId, out var trusted))
        {
            throw Fail($"the signing key '{parsed.KeyId}' is not trusted");
        }

        byte[] signed, signature;
        try
        {
            signed = Convert.FromBase64String(parsed.Signed);
            signature = Convert.FromBase64String(parsed.Signature);
        }
        catch (FormatException ex)
        {
            throw Fail("the envelope contains invalid base64", ex);
        }

        bool valid;
        try
        {
            using var key = trusted.CreateKey();
            valid = key.VerifyData(signed, signature, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            valid = false;
        }

        keyId = parsed.KeyId;
        return valid ? signed : throw Fail("the signature does not match");
    }

    private static SignedManifest Parse(byte[] signed)
    {
        try
        {
            return JsonSerializer.Deserialize<SignedManifest>(signed, CoreJson.Options) ?? throw Fail("the signed document is empty");
        }
        catch (JsonException ex)
        {
            throw Fail("the signed document is malformed: " + ex.Message, ex);
        }
    }

    private void Validate(SignedManifest manifest, string expectedChannel)
    {
        if (manifest.Format != SignedManifest.CurrentFormat)
        {
            throw Fail($"unsupported manifest format {manifest.Format}");
        }

        if (!string.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal))
        {
            throw Fail($"the document is for channel '{manifest.Channel}', not '{expectedChannel}'");
        }

        if (manifest.Version < 1 || manifest.ExpiresUtc <= manifest.IssuedUtc)
        {
            throw Fail("version or validity period is not plausible");
        }

        var now = _time.GetUtcNow();
        if (now >= manifest.ExpiresUtc)
        {
            throw Fail($"the document expired on {manifest.ExpiresUtc:u}");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            if (!IsPlainName(artifact.Name) || !names.Add(artifact.Name))
            {
                throw Fail($"artifact name '{artifact.Name}' is invalid or repeated");
            }

            if (artifact.Size < 0 || !FileHash.TryCreate(HashKind.Sha256, artifact.Sha256, out _))
            {
                throw Fail($"artifact '{artifact.Name}' has no valid size and SHA-256");
            }

            if (artifact.Url is not null && (!artifact.Url.IsAbsoluteUri || (artifact.Url.Scheme != Uri.UriSchemeHttps && artifact.Url.Scheme != Uri.UriSchemeHttp)))
            {
                throw Fail($"artifact '{artifact.Name}' has no web address");
            }
        }
    }

    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= 255
        && name != ".."
        && name.All(c => !char.IsControl(c) && c is not ('/' or '\\'));

    private static BootrixException Fail(string detail, Exception? inner = null) =>
        new(ErrorCode.SignatureInvalid, detail, inner);

    private sealed record ManifestEnvelope
    {
        public string? Format { get; init; }

        public string? Algorithm { get; init; }

        public string? KeyId { get; init; }

        public string? Signed { get; init; }

        public string? Signature { get; init; }
    }
}
