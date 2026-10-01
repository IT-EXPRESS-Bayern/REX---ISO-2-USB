// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Net.Support;

/// <summary>Signs manifests with a throw-away key, the way the offline signing step does; the repository holds no private key.</summary>
internal sealed class ManifestSigner(string keyId) : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

    public string KeyId { get; } = keyId;

    public ManifestPublicKey PublicKey => new(KeyId, _key.ExportSubjectPublicKeyInfo());

    public static string Manifest(
        string channel = "catalog",
        long version = 1,
        string issued = "2026-10-01T00:00:00Z",
        string expires = "2026-10-15T00:00:00Z",
        string payload = "{ \"distributions\": [\"ubuntu\", \"debian\"] }",
        string artifacts = "[]") =>
        string.Create(CultureInfo.InvariantCulture, $$"""
            {
              "format": 1,
              "channel": "{{channel}}",
              "version": {{version}},
              "issuedUtc": "{{issued}}",
              "expiresUtc": "{{expires}}",
              "payload": {{payload}},
              "artifacts": {{artifacts}}
            }
            """);

    public byte[] Sign(string manifestJson, string? keyIdInEnvelope = null) => Sign(Encoding.UTF8.GetBytes(manifestJson), keyIdInEnvelope);

    public byte[] Sign(byte[] signedBytes, string? keyIdInEnvelope = null)
    {
        var signature = _key.SignData(signedBytes, HashAlgorithmName.SHA384, DSASignatureFormat.Rfc3279DerSequence);
        return Envelope(signedBytes, signature, keyIdInEnvelope ?? KeyId);
    }

    public static byte[] Envelope(byte[] signedBytes, byte[] signature, string keyId, string format = "bootrix-manifest-1", string algorithm = "ecdsa-p384-sha384-der") =>
        Encoding.UTF8.GetBytes($$"""
            { "format": "{{format}}", "algorithm": "{{algorithm}}", "keyId": "{{keyId}}", "signed": "{{Convert.ToBase64String(signedBytes)}}", "signature": "{{Convert.ToBase64String(signature)}}" }
            """);

    public void Dispose() => _key.Dispose();
}

internal sealed class InMemoryVersionStore : IVersionStore
{
    private readonly Dictionary<string, long> _versions = [];

    public int Writes { get; private set; }

    public long GetHighestVersion(string channel) => _versions.GetValueOrDefault(channel);

    public void Record(string channel, long version)
    {
        if (version > GetHighestVersion(channel))
        {
            _versions[channel] = version;
            Writes++;
        }
    }
}
